using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

public class EventStreamingService : IEventStreamingService
{
    private readonly IPrinterService _printerService;
    private readonly IRequestLogService _requestLogService;
    private readonly IFeedCursorStore _cursorStore;
    private readonly ILogger<EventStreamingService> _logger;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _kitchenListeningTask;
    private bool _isListening;

    // Track processed order IDs to prevent duplicate display/print (with timestamp for cleanup).
    // Restored from disk on construction: held only in memory, every process restart began with an
    // empty set and a UtcNow-30min cursor, so the last half hour of confirmed orders was re-fetched
    // AND re-printed. Survivable while a restart meant a person relaunching the app; with the Android
    // foreground service restarting itself it would put duplicate tickets on the pass.
    private readonly Dictionary<string, DateTime> _processedOrders;

    // The subset of _processedOrders that is safe to persist. An order joins it only once the print
    // path has confirmed it (see ConfirmOrderHandled); error-log keys join immediately, since
    // "logged" completes the moment it is marked. Everything else stays in memory only, so a process
    // killed between dispatch and print re-drives the order instead of suppressing it forever.
    private readonly HashSet<string> _persistableOrders;

    // Order key -> the modifiedSince value in effect for the poll that produced it, for orders that
    // have been dispatched but not yet confirmed printed. Holding an order out of the persisted dedup
    // set is only half of not losing it: the persisted CURSOR must also stay at or before that poll's
    // window, because the backend filter is a strict `>` on CreatedAt/UpdatedAt
    // (backend PrinterFeedQuery.cs:53). Without this floor a later write — the debounce expiring,
    // another order confirming, a stop flush — advances modifiedSince past the unconfirmed order, and
    // the restart that was supposed to reprint it never fetches it at all.
    private readonly Dictionary<string, DateTime> _unconfirmedPollWindows = new();

    // Orders already reported to the operator as unrecoverable. Kept so a late confirm can retract
    // that warning exactly once. Cleared with the rest of the order's state on dedup cleanup.
    private readonly HashSet<string> _expiredUnconfirmed = new();

    private readonly object _processedOrdersLock = new();
    // Operation label on every poll-related entry in the request log / Errors page.
    private const string PollingLogOperation = "Order Polling";

    // Serialises the whole snapshot-then-write, so two threads cannot interleave such that an older
    // snapshot lands last and drops a dedup entry that a newer one had captured. Never taken while
    // holding _processedOrdersLock — always the other way round.
    private readonly object _cursorPersistLock = new();
    private const int MaxProcessedOrdersAge = 3600; // 1 hour in seconds

    /// <summary>How long a processed order stays in the dedup set. Public so the ordering invariant
    /// documented in CLAUDE.md §3 can be asserted rather than merely written down.</summary>
    public static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(MaxProcessedOrdersAge);

    // Unconfirmed orders expire well INSIDE FeedCursorStore.MaxLookBack, not on the hour-long dedup
    // window. The cursor floor an unconfirmed order holds is only honoured while it survives that
    // clamp, so retaining entries past it produced a band in which the floor was persisted and then
    // silently discarded on load — the order neither re-fetched nor reported. Derived from
    // MaxLookBack rather than hard-coded so the two cannot drift apart.
    public static readonly TimeSpan UnconfirmedRetention =
        FeedCursorStore.MaxLookBack - TimeSpan.FromMinutes(5);
    private DateTime _lastPollTime;
    private Task? _pollingTask;  // Primary polling mechanism

    // The cursor is rewritten on every advance, so the routine case is debounced; a batch that
    // actually printed something forces an immediate write, because that is the state whose loss
    // causes a duplicate ticket.
    private static readonly TimeSpan CursorSaveInterval = TimeSpan.FromSeconds(60);
    private DateTime _lastCursorSaveAt = DateTime.MinValue;

    public event EventHandler<OrderEvent>? OrderReceived;
    public event EventHandler<string>? ConnectionStatusChanged;

    public bool IsListening => _isListening;

    // Advanced only after a poll completes with a 2xx (see PollForOrdersAsync). Reported in the fleet
    // heartbeat so a "listening but not actually polling" wedge is remotely visible.
    private DateTime? _lastSuccessfulPollAt;
    public DateTime? LastSuccessfulPollAt => _lastSuccessfulPollAt;

    public EventStreamingService(
        IPrinterService printerService,
        IRequestLogService requestLogService,
        IFeedCursorStore cursorStore,
        ILogger<EventStreamingService> logger)
    {
        _printerService = printerService;
        _requestLogService = requestLogService;
        _cursorStore = cursorStore;
        _logger = logger;

        // Restored here rather than in StartListeningAsync so the cursor is already correct if
        // anything reads it before the feed starts. Load never throws; a missing or unreadable file
        // yields the default look-back.
        var cursor = _cursorStore.Load();
        _lastPollTime = cursor.LastPollTime;
        _processedOrders = cursor.ProcessedOrders;
        // Everything that was persisted had already been confirmed, so it stays persistable.
        _persistableOrders = new HashSet<string>(cursor.ProcessedOrders.Keys);
    }

    public async Task StartListeningAsync(CancellationToken cancellationToken = default)
    {
        if (_isListening)
        {
            _logger.LogWarning("EventStreamingService is already listening");
            return;
        }

        var config = await _printerService.LoadConfigurationAsync();

        // Debug logging
        _logger.LogInformation("SERVICE: Loaded API Base URL from config: {ApiUrl}", config.ApiBaseUrl);
        System.Diagnostics.Debug.WriteLine($"DEBUG SERVICE: API Base URL = {config.ApiBaseUrl}");

        if (string.IsNullOrWhiteSpace(config.ApiBaseUrl))
        {
            OnConnectionStatusChanged("Error: API Base URL not configured");
            return;
        }

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _isListening = true;

        // Use ONLY polling for maximum reliability (SSE was unreliable)
        // Poll every 5 seconds for confirmed orders
        _pollingTask = RunPollLoopAsync(config.ApiBaseUrl, _cancellationTokenSource);

        OnConnectionStatusChanged("Connected - polling for orders every 5s");
        _logger.LogInformation("Started POLLING-ONLY mode for order updates (no SSE)");
    }

    public async Task StopListeningAsync()
    {
        if (!_isListening)
        {
            return;
        }

        _logger.LogInformation("Stopping SSE listener");
        _isListening = false;

        // Flush on the way down so a clean stop (user tap, app close) does not throw away up to a
        // minute of debounced cursor and re-fetch it on the next start.
        PersistCursor(force: true);

        _cancellationTokenSource?.Cancel();

        try
        {
            if (_kitchenListeningTask != null)
            {
                await _kitchenListeningTask;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelling
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while stopping SSE listener");
        }
        finally
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _kitchenListeningTask = null;

            OnConnectionStatusChanged("Disconnected");
        }
    }

    private async Task ListenToStreamAsync(string apiBaseUrl, string endpoint, CancellationToken cancellationToken)
    {
        var url = $"{apiBaseUrl.TrimEnd('/')}/api/events/{endpoint}";
        var retryDelay = TimeSpan.FromSeconds(5);
        const int maxRetryDelay = 60;

        while (!cancellationToken.IsCancellationRequested)
        {
            HttpClient? httpClient = null;
            DateTime lastMessageReceived = DateTime.UtcNow;

            try
            {
                _logger.LogInformation("Connecting to SSE stream: {Url}", url);
                OnConnectionStatusChanged($"Connecting to {endpoint}...");

                // Create new HttpClient for each connection attempt with TCP keep-alive
                var handler = new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                    KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                    KeepAlivePingDelay = TimeSpan.FromSeconds(15),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(10)
                };
                httpClient = new HttpClient(handler)
                {
                    Timeout = Timeout.InfiniteTimeSpan
                };

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
                request.Headers.Connection.Add("keep-alive");
                request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };

                // Capture request details
                var requestHeaders = new Dictionary<string, string>();
                foreach (var header in request.Headers)
                {
                    requestHeaders[header.Key] = string.Join(", ", header.Value);
                }

                // Log SSE connection with full request details
                _requestLogService.LogSSEConnection(endpoint, "Connecting...", url, requestHeaders);

                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                // Capture response details
                var responseHeaders = new Dictionary<string, string>();
                foreach (var header in response.Headers)
                {
                    responseHeaders[header.Key] = string.Join(", ", header.Value);
                }
                foreach (var header in response.Content.Headers)
                {
                    responseHeaders[header.Key] = string.Join(", ", header.Value);
                }

                // Log SSE response with full details
                _requestLogService.LogSSEResponse(endpoint, (int)response.StatusCode, responseHeaders);

                OnConnectionStatusChanged($"Connected to {endpoint} stream");
                _logger.LogInformation("Connected to SSE stream: {Endpoint}", endpoint);

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 256, leaveOpen: true);

                string? eventType = null;
                var dataBuilder = new StringBuilder();

                // Reset retry delay and last message time on successful connection
                retryDelay = TimeSpan.FromSeconds(5);
                lastMessageReceived = DateTime.UtcNow;

                // Start background task to check for connection timeout
                var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _ = Task.Run(async () =>
                {
                    while (!timeoutCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(5000, timeoutCts.Token).ConfigureAwait(false);
                        var timeSinceLastMessage = DateTime.UtcNow - lastMessageReceived;
                        if (timeSinceLastMessage.TotalSeconds > 35)
                        {
                            _logger.LogWarning("Connection timeout - no messages for {Seconds}s, cancelling...", timeSinceLastMessage.TotalSeconds);
                            timeoutCts.Cancel();
                        }
                    }
                }, timeoutCts.Token);

                try
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync(timeoutCts.Token)) != null)
                    {
                        // Update last message time for any data received
                        lastMessageReceived = DateTime.UtcNow;

                        if (line.StartsWith("event:"))
                        {
                            eventType = line.Substring(6).Trim();

                            // Log heartbeat events but don't process them further
                            if (eventType == "heartbeat")
                            {
                                _logger.LogDebug("Heartbeat received from {Endpoint}", endpoint);
                            }
                        }
                        else if (line.StartsWith("data:"))
                        {
                            // Only collect data if it's not a heartbeat
                            if (eventType != "heartbeat")
                            {
                                dataBuilder.AppendLine(line.Substring(5).Trim());
                            }
                        }
                        else if (line.StartsWith(":"))
                        {
                            // SSE comment line - also a form of heartbeat
                            _logger.LogDebug("Comment/heartbeat received from {Endpoint}", endpoint);
                        }
                        else if (string.IsNullOrEmpty(line))
                        {
                            // Empty line indicates end of message - process event
                            if (dataBuilder.Length > 0 && eventType != "heartbeat")
                            {
                                var data = dataBuilder.ToString().Trim();
                                _logger.LogInformation("Processing SSE event: {EventType}", eventType);
                                await ProcessEventAsync(eventType ?? "message", data, endpoint, cancellationToken);
                            }

                            // Reset for next message
                            dataBuilder.Clear();
                            eventType = null;
                        }
                    }
                }
                finally
                {
                    timeoutCts.Cancel();
                    timeoutCts.Dispose();
                }

                _logger.LogWarning("SSE stream ended for {Endpoint}", endpoint);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("SSE listener cancelled for {Endpoint}", endpoint);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SSE stream for {Endpoint}", endpoint);
                OnConnectionStatusChanged($"Error: {ex.Message}");

                if (!cancellationToken.IsCancellationRequested)
                {
                    // Exponential backoff with max delay
                    _logger.LogInformation("Retrying connection in {Delay} seconds...", retryDelay.TotalSeconds);
                    await Task.Delay(retryDelay, cancellationToken);

                    retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, maxRetryDelay));
                }
            }
            finally
            {
                httpClient?.Dispose();
            }
        }
    }

    private async Task ProcessEventAsync(string eventType, string data, string sourceEndpoint, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Processing SSE event - Type: {EventType}, Source: {Source}, Data: {Data}",
                eventType, sourceEndpoint, data);

            // Handle connection event
            if (eventType == "connected")
            {
                _logger.LogInformation("Received connection confirmation from {Source}", sourceEndpoint);
                _requestLogService.LogSSEEvent("connected", $"Connection confirmed from {sourceEndpoint}", data, "Service");
                return;
            }

            // Parse order event - handle both old format and new format
            if (eventType == "order-created" || eventType == "order-updated" || eventType == "order_created" ||
                eventType == "order_updated" || eventType == "order" || eventType == "message" ||
                eventType == "order-status-changed" || eventType == "order-ready" || eventType == "order-completed")
            {
                // Log raw event with truncated data for display
                var truncatedData = data.Length > 100 ? data.Substring(0, 100) + "..." : data;

                try
                {
                    // Try to parse as OrderEvent wrapper first (new format)
                    var orderEvent = JsonSerializer.Deserialize<OrderEvent>(data, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    // Pattern match (not `orderEvent?.Order != null`) so the compiler narrows orderEvent
                    // to non-null in this block — clears CS8604 at OnOrderReceived(orderEvent) below.
                    if (orderEvent is { Order: not null })
                    {
                        var order = orderEvent.Order;

                        // FILTER: Only process orders with Confirmed status
                        if (!string.Equals(order.Status, "Confirmed", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogDebug("Skipping order {OrderNumber} - status is {Status}, not Confirmed",
                                order.OrderNumber, order.Status);
                            return;
                        }

                        // DEDUPLICATION: Check if we've already processed this order
                        var orderKey = order.OrderNumber;
                        if (IsOrderAlreadyProcessed(orderKey))
                        {
                            _logger.LogInformation("Skipping duplicate order {OrderNumber}", order.OrderNumber);
                            return;
                        }

                        // Mark order as processed
                        MarkOrderAsProcessed(orderKey);

                        // Log order details for debugging
                        _logger.LogInformation("Received order {OrderNumber} with {ItemCount} items (Status: {Status})",
                            order.OrderNumber,
                            order.Items?.Count ?? 0,
                            order.Status);

                        if (order.Items != null && order.Items.Any())
                        {
                            foreach (var item in order.Items)
                            {
                                _logger.LogInformation("  - Item: {Quantity}x {ProductName}",
                                    item.Quantity, item.ProductName);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Order {OrderNumber} has no items! Attempting to fetch full details from API...", order.OrderNumber);

                            try
                            {
                                // Extract ID from OrderNumber (e.g., "ORD-123" or "123")
                                var orderIdStr = order.OrderNumber.Contains("/")
                                    ? order.OrderNumber.Split('/').Last()
                                    : order.OrderNumber;

                                if (int.TryParse(orderIdStr, out var orderId))
                                {
                                    var fullOrder = await FetchOrderDetailsAsync(orderId, sourceEndpoint);
                                    if (fullOrder != null && fullOrder.Items != null && fullOrder.Items.Any())
                                    {
                                        _logger.LogInformation("Successfully fetched full details for order {OrderNumber} with {Count} items",
                                            order.OrderNumber, fullOrder.Items.Count);
                                        order = fullOrder;
                                        // Update the wrapper reference too (orderEvent is non-null in this
                                        // block — see the `is { Order: not null }` guard above).
                                        orderEvent.Order = fullOrder;
                                    }
                                    else
                                    {
                                        _logger.LogWarning("Failed to fetch items for order {OrderNumber} from API", order.OrderNumber);
                                    }
                                }
                            }
                            catch (Exception fetchEx)
                            {
                                _logger.LogError(fetchEx, "Error fetching full order details for {OrderNumber}", order.OrderNumber);
                            }
                        }

                        // Log parsed order with full JSON data
                        _requestLogService.LogOrderReceived(
                            int.TryParse(order.OrderNumber.Split('/').Last(), out var orderNum) ? orderNum : 0,
                            order.TableNumber,
                            order.Total,
                            data,
                            "Service");

                        // Notify subscribers
                        OnOrderReceived(orderEvent);
                        return;
                    }
                }
                catch
                {
                    // If that fails, try to parse as Order directly (old format fallback)
                    var order = JsonSerializer.Deserialize<Order>(data, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (order != null)
                    {
                        // FILTER: Only process orders with Confirmed status
                        if (!string.Equals(order.Status, "Confirmed", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogDebug("Skipping order {OrderNumber} - status is {Status}, not Confirmed",
                                order.OrderNumber, order.Status);
                            return;
                        }

                        // DEDUPLICATION: Check if we've already processed this order
                        var orderKey = order.OrderNumber;
                        if (IsOrderAlreadyProcessed(orderKey))
                        {
                            _logger.LogInformation("Skipping duplicate order {OrderNumber}", order.OrderNumber);
                            return;
                        }

                        // Mark order as processed
                        MarkOrderAsProcessed(orderKey);

                        // Log parsed order with full JSON data
                        _requestLogService.LogOrderReceived(
                            int.TryParse(order.OrderNumber.Split('/').Last(), out var orderNum) ? orderNum : 0,
                            order.TableNumber,
                            order.Total,
                            data,
                            "Service");

                        var orderEvent = new OrderEvent
                        {
                            EventType = eventType,
                            Order = order,
                            Timestamp = DateTime.UtcNow
                        };

                        // Notify subscribers
                        OnOrderReceived(orderEvent);
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse order data: {Data}", data);
            _requestLogService.LogError("JSON Parse Error", $"Failed to parse event data: {ex.Message}", data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing SSE event");
            _requestLogService.LogError("Event Processing Error", ex.Message, ex.StackTrace);
        }
    }

    protected virtual void OnOrderReceived(OrderEvent orderEvent)
    {
        OrderReceived?.Invoke(this, orderEvent);
    }

    protected virtual void OnConnectionStatusChanged(string status)
    {
        ConnectionStatusChanged?.Invoke(this, status);
    }

    /// <summary>
    /// Check if an order has already been processed (to prevent duplicates)
    /// </summary>
    private bool IsOrderAlreadyProcessed(string orderNumber)
    {
        lock (_processedOrdersLock)
        {
            // Clean up old entries first
            CleanupOldProcessedOrders();

            return _processedOrders.ContainsKey(orderNumber);
        }
    }

    /// <summary>
    /// Mark an order as processed
    /// </summary>
    /// <param name="persistable">
    /// True for entries that are complete the moment they are marked (error-log keys). Order keys
    /// pass false and become persistable only via <see cref="ConfirmOrderHandled"/>, once printed.
    /// Taken in the same lock acquisition as the mark: doing it in a second acquisition let
    /// <see cref="CleanupOldProcessedOrders"/> run in the gap and evict the key, leaving an orphan in
    /// <see cref="_persistableOrders"/> that cleanup could never remove.
    /// </param>
    /// <param name="unconfirmedPollWindow">
    /// For a dispatched order, the modifiedSince value its poll used — recorded so the persisted
    /// cursor can be floored to it until the print path confirms the order. Written in the SAME lock
    /// acquisition as the mark: a second acquisition would let cleanup evict the key in the gap and
    /// strand an entry that nothing can ever remove, which here would pin the persisted cursor
    /// permanently (the floor iterates these values unconditionally).
    /// </param>
    private void MarkOrderAsProcessed(
        string orderNumber, bool persistable = false, DateTime? unconfirmedPollWindow = null)
    {
        lock (_processedOrdersLock)
        {
            _processedOrders[orderNumber] = DateTime.UtcNow;

            if (persistable)
            {
                _persistableOrders.Add(orderNumber);
            }

            if (unconfirmedPollWindow is { } window)
            {
                _unconfirmedPollWindows[orderNumber] = window;
            }
        }
    }

    /// <summary>
    /// Writes the poll cursor and the dedup set to disk so a process restart resumes instead of
    /// re-fetching (and re-printing) the last half hour.
    /// </summary>
    /// <param name="force">
    /// Bypasses the debounce. Pass true whenever the dedup set changed — that is the state whose
    /// loss causes a duplicate ticket. The routine per-poll cursor advance is debounced because it
    /// happens every 5 seconds and losing a little of it is harmless.
    /// </param>
    private void PersistCursor(bool force)
    {
        // Snapshot AND write under one lock. Holding it only for the snapshot let a slower thread's
        // older copy win the file race and drop a dedup entry a newer copy had already captured —
        // which would reprint that order after the next restart, the exact thing this exists to stop.
        lock (_cursorPersistLock)
        {
            if (!force && DateTime.UtcNow - _lastCursorSaveAt < CursorSaveInterval)
            {
                return;
            }

            _lastCursorSaveAt = DateTime.UtcNow;

            FeedCursor snapshot;
            lock (_processedOrdersLock)
            {
                // Never persist a cursor past the earliest still-unconfirmed order's poll window.
                // Min over those windows AND the live cursor, so with nothing unconfirmed it is just
                // the cursor.
                var persistedLastPoll = _unconfirmedPollWindows.Values.Append(_lastPollTime).Min();

                snapshot = new FeedCursor
                {
                    LastPollTime = persistedLastPoll,
                    // Confirmed entries only — see _persistableOrders.
                    ProcessedOrders = _processedOrders
                        .Where(kvp => _persistableOrders.Contains(kvp.Key))
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                };
            }

            _cursorStore.Save(snapshot);
        }
    }

    /// <inheritdoc />
    public void ConfirmOrderHandled(string orderNumber)
    {
        if (string.IsNullOrEmpty(orderNumber))
        {
            return;
        }

        bool retracting;
        lock (_processedOrdersLock)
        {
            // Only for orders we actually dispatched; a confirmation for something never marked
            // would otherwise create a persistable entry with no timestamp to age out on.
            if (!_processedOrders.ContainsKey(orderNumber))
            {
                return;
            }

            _persistableOrders.Add(orderNumber);
            // Printed, so the cursor no longer has to be held back for it. Remove reports whether the
            // entry was still there: if it was not, expiry already told the operator this order "may
            // not have printed", and acting on that stale advice is how they end up with the very
            // duplicate ticket this all exists to prevent.
            retracting = !_unconfirmedPollWindows.Remove(orderNumber)
                && _expiredUnconfirmed.Remove(orderNumber);
        }

        if (retracting)
        {
            _logger.LogInformation(
                "Order {OrderNumber} printed after being reported unrecoverable", orderNumber);
            _requestLogService.LogWarning(
                "Order Polling",
                $"Order {orderNumber} did print after all — ignore the earlier warning",
                "It completed later than expected. Do not reprint it; that would produce a duplicate ticket.");
        }

        // Forced: this is the write that makes the difference between a duplicate ticket and none.
        PersistCursor(force: true);
    }

    /// <summary>
    /// Clean up processed orders older than MaxProcessedOrdersAge, and expire unconfirmed orders on
    /// the shorter <see cref="UnconfirmedRetention"/>.
    /// </summary>
    private void CleanupOldProcessedOrders()
    {
        ExpireUnrecoverableUnconfirmedOrders();

        var cutoff = DateTime.UtcNow.AddSeconds(-MaxProcessedOrdersAge);
        var oldOrders = _processedOrders
            .Where(kvp => kvp.Value < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var orderNumber in oldOrders)
        {
            _processedOrders.Remove(orderNumber);
            _persistableOrders.Remove(orderNumber);
            _unconfirmedPollWindows.Remove(orderNumber);
            _expiredUnconfirmed.Remove(orderNumber);
        }

        if (oldOrders.Count > 0)
        {
            _logger.LogDebug("Cleaned up {Count} old processed order records", oldOrders.Count);
        }
    }

    /// <summary>
    /// Drops unconfirmed orders whose poll window has aged past <see cref="UnconfirmedRetention"/>,
    /// and reports each one — because past that point it genuinely cannot be recovered.
    /// <para>The cursor floor only works while the floored value survives
    /// <see cref="FeedCursorStore.MaxLookBack"/>, which clamps any restored cursor forward. Retaining
    /// unconfirmed entries for the full dedup hour therefore created a silent band: an order stuck
    /// unconfirmed for 30–60 minutes had a floor that was computed, persisted, and then discarded by
    /// the very load it existed to influence — so it was neither re-fetched nor reported. Expiring
    /// inside the clamp closes the band, and anything that still ages out is a genuinely dropped
    /// ticket the operator has to be told about rather than left to discover from a customer.</para>
    /// </summary>
    private void ExpireUnrecoverableUnconfirmedOrders()
    {
        if (_unconfirmedPollWindows.Count == 0)
        {
            return;
        }

        var expired = UnconfirmedOrderExpiry.Expired(
            _unconfirmedPollWindows, DateTime.UtcNow, UnconfirmedRetention);

        foreach (var orderNumber in expired)
        {
            _unconfirmedPollWindows.Remove(orderNumber);
            _expiredUnconfirmed.Add(orderNumber);

            _logger.LogError(
                "Order {OrderNumber} was never confirmed printed and can no longer be recovered",
                orderNumber);
            // Both surfaces are non-blocking (they marshal to the UI thread), so reporting from
            // inside the dedup lock does not stall the poll loop.
            _requestLogService.LogError(
                "Order Polling",
                $"Order {orderNumber} may not have printed, and is too old to fetch again",
                "The order was received but never confirmed printed. Check the printer and reprint " +
                "it from the Orders tab if the ticket is missing.");
        }
    }
    private async Task<Order?> FetchOrderDetailsAsync(int orderId, string sourceEndpoint)
    {
        try
        {
            var config = await _printerService.LoadConfigurationAsync();
            if (string.IsNullOrWhiteSpace(config.ApiBaseUrl)) return null;

            var url = $"{config.ApiBaseUrl.TrimEnd('/')}/api/orders/{orderId}";
            _logger.LogDebug("Fetching order details from: {Url}", url);

            using var httpClient = new HttpClient();
            // The order-details endpoint needs the same X-Api-Key as the printer feed. Without it,
            // this fallback returns 401 and silently drops the enrichment for item-less events.
            if (!string.IsNullOrWhiteSpace(config.ApiKey))
            {
                httpClient.DefaultRequestHeaders.Add("X-Api-Key", config.ApiKey);
            }

            var response = await httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch order {OrderId}: {StatusCode}", orderId, response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var order = JsonSerializer.Deserialize<Order>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return order;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception fetching order {OrderId}", orderId);
            return null;
        }
    }
    /// <summary>
    /// POLLING-ONLY mechanism - polls for confirmed orders every 5 seconds
    /// SSE was unreliable, so we use pure polling for guaranteed delivery
    /// </summary>
    /// <summary>
    /// Runs the poll loop and guarantees <see cref="IsListening"/> is cleared once it ends, however
    /// it ends. Before this, <see cref="StopListeningAsync"/> was the only writer of the flag, so a
    /// loop that exited by cancellation left the service reporting "listening" over a dead loop —
    /// and since every restart path (<c>StartListeningAsync</c>, <c>OrderPipeline.StartAsync</c>,
    /// <c>OrderPipeline.InitializeAsync</c>) early-returns when <c>IsListening</c> is true, the feed
    /// could not be recovered without a process restart.
    /// </summary>
    private async Task RunPollLoopAsync(string apiBaseUrl, CancellationTokenSource ownCts)
    {
        try
        {
            await PollForOrdersAsync(apiBaseUrl, ownCts.Token);
        }
        catch (Exception ex)
        {
            // The loop guards each iteration, so reaching here means something escaped one of its own
            // handlers (or the prologue) and the feed has stopped for good. The finally below makes
            // that state honest; without this catch it would also be silent, since _pollingTask is
            // never awaited — a quiet, unexplained feed is the hardest failure to support remotely.
            // Not SentrySdk here: this file is deliberately free of both MAUI and Sentry so it can be
            // source-linked into the plain net10.0 test project. Remote visibility is already covered
            // — IsListening flips false below and the heartbeat reports it alongside
            // LastSuccessfulPollAt, which is exactly the "listening but not polling" signal that
            // pair exists to carry.
            _logger.LogError(ex, "Poll loop terminated unexpectedly");
            _requestLogService.LogError(
                PollingLogOperation, "The order feed stopped unexpectedly and is no longer polling", ex.Message);
            OnConnectionStatusChanged($"Stopped: {ex.Message}");
        }
        finally
        {
            // Only if a newer StartListeningAsync has not already replaced this loop, so a late-
            // finishing old loop cannot clear the new one's flag.
            if (ReferenceEquals(_cancellationTokenSource, ownCts))
            {
                _isListening = false;
            }
        }
    }

    private async Task PollForOrdersAsync(string apiBaseUrl, CancellationToken cancellationToken)
    {
        const int pollingIntervalSeconds = 5;
        var baseUrl = apiBaseUrl.TrimEnd('/');

        _logger.LogInformation("========================================");
        _logger.LogInformation("🔄 POLLING SERVICE STARTED");
        _logger.LogInformation("   API Base URL: {Url}", baseUrl);
        _logger.LogInformation("   Interval: {Interval} seconds", pollingIntervalSeconds);
        _logger.LogInformation("========================================");

        // Also log to debug output for WPF apps
        System.Diagnostics.Debug.WriteLine($"[POLLING] Started - URL: {baseUrl}, Interval: {pollingIntervalSeconds}s");

        int pollCount = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(pollingIntervalSeconds), cancellationToken);

                pollCount++;
                // Dedicated printer-feed endpoint. Auth is the X-Api-Key header added below —
                // required in production (per-tenant key set in Settings); a missing/incorrect key
                // returns 401, surfaced as "Poll failed: Unauthorized" and logged to the Errors page.
                // The exact value this request filters on — recorded against any order it yields so
                // the persisted cursor can be floored to it until that order is confirmed.
                DateTime pollWindowStart;
                lock (_processedOrdersLock)
                {
                    pollWindowStart = _lastPollTime;
                }

                var pollUrl = $"{baseUrl}/api/orders/printer-feed?modifiedSince={pollWindowStart:o}";

                _logger.LogInformation("🔄 Poll #{Count} - Fetching orders since {Since}", pollCount, pollWindowStart);
                System.Diagnostics.Debug.WriteLine($"[POLLING] #{pollCount} - URL: {pollUrl}");

                // Update connection status so UI shows activity
                OnConnectionStatusChanged($"Polling... (#{pollCount})");

                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

                // Add X-Api-Key header if configured
                var config = await _printerService.LoadConfigurationAsync();
                if (!string.IsNullOrWhiteSpace(config.ApiKey))
                {
                    httpClient.DefaultRequestHeaders.Add("X-Api-Key", config.ApiKey);
                    _logger.LogInformation("   Using API key for authentication");
                }
                else
                {
                    _logger.LogWarning("   ⚠️ No API key configured - request may fail if auth required");
                }

                var response = await httpClient.GetAsync(pollUrl, cancellationToken);

                _logger.LogInformation("   Response: {StatusCode}", response.StatusCode);
                System.Diagnostics.Debug.WriteLine($"[POLLING] Response: {response.StatusCode}");

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    var bodyPreview = errorBody.Substring(0, Math.Min(200, errorBody.Length));
                    _logger.LogWarning("❌ Polling failed: {StatusCode} - {Body}", response.StatusCode, bodyPreview);

                    // Also surface poll failures on the Errors page. Previously these went only to the
                    // ILogger, so the Errors tab stayed empty while the feed silently 401'd — leaving
                    // the field with no diagnostic trail. A 401 is almost always a missing/incorrect
                    // API key, so spell that out to make it actionable.
                    var detail = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        ? "The API key is missing or incorrect. Enter the printer API key in Settings, then Save."
                        : bodyPreview;
                    _requestLogService.LogError(PollingLogOperation, $"Poll failed: {response.StatusCode}", detail);

                    OnConnectionStatusChanged($"Poll failed: {response.StatusCode}");
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogInformation("   Response length: {Length} bytes", json.Length);

                // Deserialise each order independently. Before PR #61 the whole batch was deserialised in
                // one call, so a single un-readable order (e.g. deliveryAddress typed string vs object)
                // threw for the entire batch; _lastPollTime never advanced, so every 5s poll re-threw on
                // the same order forever and nothing printed until restart. Now one bad order is logged +
                // skipped (surfaced on the Errors/Diagnostics page) and the rest of the batch still prints.
                var parseResult = OrderFeedParser.Parse(json);

                // Set only where an entry is genuinely ADDED to the persistable set — an already-known
                // failure hits `continue` below and changes nothing, so inferring this from
                // parseResult.Errors would force a full write on every poll for as long as the
                // backend keeps re-emitting the same bad order, defeating the debounce entirely.
                var dedupChanged = false;

                foreach (var failure in parseResult.Errors)
                {
                    // If the bad order has an extractable number, dedupe the error log through the same
                    // window as good orders so a re-emitted bad order doesn't re-log the identical error
                    // every 5s and flood the Errors/Diagnostics page. The key is prefixed "error:" so it
                    // stays isolated from the print-dedup pool: if the backend later fixes the order and
                    // re-emits it, it must still print rather than be skipped as an already-processed
                    // duplicate. Unidentifiable failures can't be deduped, so they log each poll (rare — a
                    // whole-body/envelope failure, not a routine per-order drift).
                    if (!string.IsNullOrEmpty(failure.OrderNumber))
                    {
                        var errorKey = "error:" + failure.OrderNumber;
                        if (IsOrderAlreadyProcessed(errorKey))
                        {
                            continue;
                        }
                        // "Logged" completes at the moment of marking, so unlike a print this is safe
                        // to persist straight away — it stops a re-emitted bad order re-logging the
                        // identical error after every restart.
                        MarkOrderAsProcessed(errorKey, persistable: true);
                        dedupChanged = true;
                    }

                    var who = !string.IsNullOrEmpty(failure.OrderNumber)
                        ? $"order {failure.OrderNumber}"
                        : failure.Index >= 0 ? $"order at index {failure.Index}" : "the feed response";
                    _logger.LogError("⚠️ Skipping un-deserialisable {Who}: {Message}", who, failure.Message);
                    _requestLogService.LogError(
                        PollingLogOperation,
                        $"Skipped an order that could not be read from the feed ({who})",
                        failure.Message);
                }

                var itemCount = parseResult.Orders.Count;
                _logger.LogInformation("   Orders found: {Count}", itemCount);

                if (parseResult.Orders.Count > 0)
                {
                    _logger.LogInformation("📦 Found {Count} confirmed orders!", parseResult.Orders.Count);

                    foreach (var order in parseResult.Orders)
                    {
                        _logger.LogInformation("   Processing order: {OrderNumber} (Status: {Status})",
                            order.OrderNumber, order.Status);

                        if (IsOrderAlreadyProcessed(order.OrderNumber))
                        {
                            _logger.LogInformation("   ⏭️ Skipping duplicate: {OrderNumber}", order.OrderNumber);
                            continue;
                        }

                        // Deliberately NOT dedupChanged: this order is not persistable until the
                        // print path confirms it (ConfirmOrderHandled), which forces its own write.
                        MarkOrderAsProcessed(order.OrderNumber, unconfirmedPollWindow: pollWindowStart);

                        var orderEvent = new OrderEvent
                        {
                            EventType = "order-polled",
                            Order = order,
                            Timestamp = DateTime.UtcNow
                        };

                        _logger.LogInformation("�️ Sending order to printer: {OrderNumber}", order.OrderNumber);
                        OnOrderReceived(orderEvent);
                    }
                }
                else
                {
                    _logger.LogInformation("   No new orders");
                }

                lock (_processedOrdersLock)
                {
                    _lastPollTime = DateTime.UtcNow;
                }

                _lastSuccessfulPollAt = DateTime.UtcNow;

                // Written before the next poll can advance the cursor again. Forced when this batch
                // marked something processed: losing that entry to a crash is what makes an order
                // print twice, whereas losing a few seconds of cursor only costs a re-fetch that the
                // dedup set then absorbs.
                PersistCursor(force: dedupChanged);

                OnConnectionStatusChanged($"Connected - last poll: {DateTime.Now:HH:mm:ss}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("🛑 Polling cancelled");
                break;
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(httpEx, "❌ Network error during polling");
                _requestLogService.LogError(PollingLogOperation, "Network error while polling for orders", httpEx.Message);
                OnConnectionStatusChanged($"Network error: {httpEx.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error during polling");
                OnConnectionStatusChanged($"Error: {ex.Message}");
            }
        }

        _logger.LogInformation("🛑 Polling service stopped");
    }
}
