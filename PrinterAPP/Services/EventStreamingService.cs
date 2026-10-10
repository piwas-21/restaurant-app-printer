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
    private readonly IPrintUpdateJobStore? _updateJobStore;
    private readonly IDeviceIdentityService? _deviceIdentity;
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
    private string? _lastUpdateCursor;
    private Task? _pollingTask;  // Primary polling mechanism

    /// <summary>How long the feed waits between polls. Injectable so tests can drive the loop at a
    /// realistic shape without paying its wall clock; production always uses the 5 s default.</summary>
    private readonly TimeSpan _pollInterval;
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    // The cursor is rewritten on every advance, so the routine case is debounced; a batch that
    // actually printed something forces an immediate write, because that is the state whose loss
    // causes a duplicate ticket.
    private static readonly TimeSpan CursorSaveInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryFloorAllowance = TimeSpan.FromSeconds(1);
    private DateTime _lastCursorSaveAt = DateTime.MinValue;
    private bool _cursorSavePending;

    public event EventHandler<OrderEvent>? OrderReceived;
    public event EventHandler<PrinterFeedUpdate>? UpdateReceived;
    public event EventHandler<string>? ConnectionStatusChanged;

    public bool IsListening => _isListening;

    // Advanced only after a poll completes with a 2xx (see PollForOrdersAsync). Reported in the fleet
    // heartbeat so a "listening but not actually polling" wedge is remotely visible.
    private DateTime? _lastSuccessfulPollAt;
    public DateTime? LastSuccessfulPollAt => _lastSuccessfulPollAt;

    /// <param name="pollInterval">Optional override for the gap between polls. Defaults to 5 s, which
    /// is what every production caller gets — MauiProgram registers the type by interface and the
    /// DI container fills the default. Tests pass a tiny value so the loop's shape is exercised
    /// without waiting out real seconds.</param>
    public EventStreamingService(
        IPrinterService printerService,
        IRequestLogService requestLogService,
        IFeedCursorStore cursorStore,
        ILogger<EventStreamingService> logger,
        TimeSpan? pollInterval = null,
        IPrintUpdateJobStore? updateJobStore = null,
        IDeviceIdentityService? deviceIdentity = null)
    {
        _printerService = printerService;
        _requestLogService = requestLogService;
        _cursorStore = cursorStore;
        _updateJobStore = updateJobStore;
        _deviceIdentity = deviceIdentity;
        _logger = logger;
        _pollInterval = pollInterval is { } supplied && supplied > TimeSpan.Zero
            ? supplied
            : DefaultPollInterval;

        // Restored here rather than in StartListeningAsync so the cursor is already correct if
        // anything reads it before the feed starts. Load never throws; a missing or unreadable file
        // yields the default look-back.
        var cursor = _cursorStore.Load();
        _lastPollTime = cursor.LastPollTime;
        // The update store is the sole owner of updateCursor. FeedCursor.LastUpdateCursor remains a
        // migration fallback for instances created without the update store (legacy tests/hosts).
        _lastUpdateCursor = _updateJobStore?.LoadUpdateCursor() ?? cursor.LastUpdateCursor;
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
        // Poll immediately, then every _pollInterval (5 s in production) for confirmed orders
        _pollingTask = RunPollLoopAsync(config.ApiBaseUrl, _cancellationTokenSource);

        OnConnectionStatusChanged(
            $"Connected - polling for orders every {_pollInterval.TotalSeconds:0.##}s");
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

            if (_pollingTask != null)
            {
                await _pollingTask;
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
            _pollingTask = null;

            OnConnectionStatusChanged("Disconnected");
        }
    }

    // Internal for the source-linked contract test: this keeps the SSE framing and route payload
    // dispatch under test without making the obsolete streaming loop part of the public API.
    internal async Task ListenToStreamAsync(string apiBaseUrl, string endpoint, CancellationToken cancellationToken)
    {
        var url = $"{apiBaseUrl.TrimEnd('/')}/api/events/{endpoint}";
        var retryDelay = TimeSpan.FromSeconds(5);
        const int maxRetryDelay = 60;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ListenToStreamConnectionAsync(url, endpoint, cancellationToken);
                retryDelay = TimeSpan.FromSeconds(5);
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
        }
    }

    private async Task ListenToStreamConnectionAsync(
        string url,
        string endpoint,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Connecting to SSE stream: {Url}", url);
        OnConnectionStatusChanged($"Connecting to {endpoint}...");

        using var httpClient = CreateSseHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Connection.Add("keep-alive");
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        _requestLogService.LogSSEConnection(endpoint, "Connecting...", url, HeaderSnapshot(request.Headers));

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        _requestLogService.LogSSEResponse(
            endpoint, (int)response.StatusCode,
            HeaderSnapshot(response.Headers, response.Content.Headers));

        OnConnectionStatusChanged($"Connected to {endpoint} stream");
        _logger.LogInformation("Connected to SSE stream: {Endpoint}", endpoint);
        await ConsumeSseStreamAsync(response, endpoint, cancellationToken);
        _logger.LogWarning("SSE stream ended for {Endpoint}", endpoint);
    }

    private static HttpClient CreateSseHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            KeepAlivePingDelay = TimeSpan.FromSeconds(15),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static Dictionary<string, string> HeaderSnapshot(
        params System.Net.Http.Headers.HttpHeaders[] headers)
    {
        var snapshot = new Dictionary<string, string>();
        foreach (var collection in headers)
        {
            foreach (var header in collection)
                snapshot[header.Key] = string.Join(", ", header.Value);
        }
        return snapshot;
    }

    private async Task ConsumeSseStreamAsync(
        HttpResponseMessage response,
        string endpoint,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 256, leaveOpen: true);
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var state = new SseMessageState();
        var lastMessageReceived = DateTime.UtcNow;
        _ = MonitorSseTimeoutAsync(timeoutCts, () => lastMessageReceived);

        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(timeoutCts.Token)) is not null)
            {
                lastMessageReceived = DateTime.UtcNow;
                await ProcessSseLineAsync(line, state, endpoint, cancellationToken);
            }
        }
        finally
        {
            timeoutCts.Cancel();
            timeoutCts.Dispose();
        }
    }

    private async Task ProcessSseLineAsync(
        string line,
        SseMessageState state,
        string endpoint,
        CancellationToken cancellationToken)
    {
        if (line.StartsWith("event:"))
        {
            state.EventType = line.Substring(6).Trim();
            if (state.EventType == "heartbeat")
                _logger.LogDebug("Heartbeat received from {Endpoint}", endpoint);
            return;
        }

        if (line.StartsWith("data:"))
        {
            if (state.EventType != "heartbeat")
                state.Data.AppendLine(line.Substring(5).Trim());
            return;
        }

        if (line.StartsWith(':'))
        {
            _logger.LogDebug("Comment/heartbeat received from {Endpoint}", endpoint);
            return;
        }

        if (string.IsNullOrEmpty(line))
        {
            if (state.Data.Length > 0 && state.EventType != "heartbeat")
            {
                var data = state.Data.ToString().Trim();
                _logger.LogInformation("Processing SSE event: {EventType}", state.EventType);
                await ProcessEventAsync(state.EventType ?? "message", data, endpoint, cancellationToken);
            }
            state.Data.Clear();
            state.EventType = null;
        }
    }

    private async Task MonitorSseTimeoutAsync(
        CancellationTokenSource timeoutCts,
        Func<DateTime> lastMessage)
    {
        while (!timeoutCts.Token.IsCancellationRequested)
        {
            await Task.Delay(5000, timeoutCts.Token).ConfigureAwait(false);
            var elapsed = DateTime.UtcNow - lastMessage();
            if (elapsed.TotalSeconds > 35)
            {
                _logger.LogWarning("Connection timeout - no messages for {Seconds}s, cancelling...",
                    elapsed.TotalSeconds);
                timeoutCts.Cancel();
            }
        }
    }

    private sealed class SseMessageState
    {
        public string? EventType { get; set; }
        public StringBuilder Data { get; } = new();
    }

    internal Task ProcessEventAsync(string eventType, string data, string sourceEndpoint, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Processing SSE event - Type: {EventType}, Source: {Source}, Data: {Data}",
                eventType, sourceEndpoint, data);

            if (eventType == "connected")
            {
                _logger.LogInformation("Received connection confirmation from {Source}", sourceEndpoint);
                _requestLogService.LogSSEEvent("connected", $"Connection confirmed from {sourceEndpoint}", data, "Service");
            }
            else if (IsOrderEventType(eventType))
            {
                ProcessOrderPayload(eventType, data, sourceEndpoint);
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

        return Task.CompletedTask;
    }

    private static bool IsOrderEventType(string eventType) =>
        eventType is "order-created" or "order-updated" or "order_created" or "order_updated"
            or "order" or "message" or "order-status-changed" or "order-ready" or "order-completed";

    private void ProcessOrderPayload(string eventType, string data, string sourceEndpoint)
    {
        try
        {
            var orderEvent = JsonSerializer.Deserialize<OrderEvent>(data, PrinterJsonSerialization.Options);
            if (orderEvent?.Order is null)
                throw new JsonException("The SSE payload did not contain an Order wrapper.");

            HandleOrder(orderEvent, data, sourceEndpoint, includeItemDiagnostics: true);
            return;
        }
        catch (JsonException)
        {
            // The direct Order shape is the legacy feed contract.
        }

        var order = JsonSerializer.Deserialize<Order>(data, PrinterJsonSerialization.Options);
        if (order is not null)
        {
            HandleOrder(new OrderEvent
            {
                EventType = eventType,
                Order = order,
                Timestamp = DateTime.UtcNow,
            }, data, sourceEndpoint, includeItemDiagnostics: false);
        }
    }

    private void HandleOrder(
        OrderEvent orderEvent,
        string data,
        string sourceEndpoint,
        bool includeItemDiagnostics)
    {
        if (orderEvent.Order is not { } order)
            return;
        if (!OrderRoutingStateValidation.TryValidate(order, out var routeError))
        {
            LogMalformedRoute(order.OrderNumber, routeError);
            return;
        }

        if (!string.Equals(order.Status, "Confirmed", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Skipping order {OrderNumber} - status is {Status}, not Confirmed",
                order.OrderNumber, order.Status);
            return;
        }

        if (IsOrderAlreadyProcessed(order.OrderNumber))
        {
            _logger.LogInformation("Skipping duplicate order {OrderNumber}", order.OrderNumber);
            return;
        }

        MarkOrderAsProcessed(order.OrderNumber);
        if (includeItemDiagnostics)
            LogOrderItemDiagnostics(order, sourceEndpoint);

        _requestLogService.LogOrderReceived(
            order.OrderNumber, order.TableId, order.TableLabel, order.TableNumber,
            order.Total, data, "Service");
        OnOrderReceived(orderEvent);
    }

    private void LogOrderItemDiagnostics(Order order, string sourceEndpoint)
    {
        _logger.LogInformation("Received order {OrderNumber} with {ItemCount} items (Status: {Status})",
            order.OrderNumber, order.Items?.Count ?? 0, order.Status);
        if (order.Items is { Count: > 0 })
        {
            foreach (var item in order.Items)
                _logger.LogInformation("  - Item: {Quantity}x {ProductName}", item.Quantity, item.ProductName);
            return;
        }

        _logger.LogWarning("Order {OrderNumber} arrived from {Endpoint} with no items",
            order.OrderNumber, sourceEndpoint);
        _requestLogService.LogWarning(
            PollingLogOperation,
            $"Order {order.OrderNumber} arrived with no line items — its cashier receipt will print blank",
            "The device cannot recover the missing lines; it prints what the feed sent. " +
            "Check this order in the dashboard and reprint it from the Orders tab if the ticket is wrong.",
            sourceEndpoint);
    }

    protected virtual void OnOrderReceived(OrderEvent orderEvent)
    {
        OrderReceived?.Invoke(this, orderEvent);
    }

    private void LogMalformedRoute(string orderNumber, string? reason)
    {
        var message = reason ?? "Invalid printer routing state.";
        _logger.LogError("Rejected routed SSE order {OrderNumber}: {Reason}", orderNumber, message);
        _requestLogService.LogError(
            "Printer Routing", $"Rejected routed order {orderNumber}", message + " Retry will be attempted.");
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
    private void PersistCursor(
        bool force,
        DateTime? proposedLastPollTime = null,
        string? proposedUpdateCursor = null,
        bool hasProposedUpdateCursor = false)
    {
        // Snapshot AND write under one lock. Holding it only for the snapshot let a slower thread's
        // older copy win the file race and drop a dedup entry a newer copy had already captured.
        lock (_cursorPersistLock)
        {
            if (!force && !_cursorSavePending && DateTime.UtcNow - _lastCursorSaveAt < CursorSaveInterval)
            {
                // The in-memory cursor may move during the debounce; only the durable snapshot is
                // delayed. Update cursors that changed are always forced by the caller.
                lock (_processedOrdersLock)
                {
                    if (proposedLastPollTime is not null)
                        _lastPollTime = proposedLastPollTime.Value;
                    if (hasProposedUpdateCursor && _updateJobStore is null)
                        _lastUpdateCursor = proposedUpdateCursor;
                }
                return;
            }

            DateTime liveLastPoll;
            string? liveUpdateCursor;
            FeedCursor snapshot;
            lock (_processedOrdersLock)
            {
                liveLastPoll = _lastPollTime;
                liveUpdateCursor = _lastUpdateCursor;
                var targetLastPoll = proposedLastPollTime ?? liveLastPoll;
                var targetUpdateCursor = hasProposedUpdateCursor
                    ? proposedUpdateCursor
                    : liveUpdateCursor;
                // Never persist a cursor past the earliest still-unconfirmed order's poll window.
                ExpireUnrecoverableUnconfirmedOrders();
                var persistedLastPoll = _unconfirmedPollWindows.Values.Append(targetLastPoll).Min();

                snapshot = new FeedCursor
                {
                    LastPollTime = persistedLastPoll,
                    ProcessedOrders = _processedOrders
                        .Where(kvp => _persistableOrders.Contains(kvp.Key))
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                    LastUpdateCursor = targetUpdateCursor,
                };
            }

            if (!_cursorStore.TrySave(snapshot))
            {
                _cursorSavePending = true;
                _logger.LogWarning("Could not persist feed cursor; retaining the prior in-memory position");
                return;
            }

            _cursorSavePending = false;
            _lastCursorSaveAt = DateTime.UtcNow;
            lock (_processedOrdersLock)
            {
                if (proposedLastPollTime is not null)
                    _lastPollTime = proposedLastPollTime.Value;
                if (hasProposedUpdateCursor && _updateJobStore is null)
                    _lastUpdateCursor = proposedUpdateCursor;
            }
            return;
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
                PollingLogOperation,
                $"Order {orderNumber} did print after all — ignore the earlier warning",
                "It completed later than expected. Do not reprint it; that would produce a duplicate ticket.");
        }

        // Forced: this is the write that makes the difference between a duplicate ticket and none.
        PersistCursor(force: true);
    }

    /// <inheritdoc />
    public void ReleaseOrderForRetry(
        string orderNumber,
        DateTime? createdAt = null,
        DateTime? updatedAt = null)
    {
        if (string.IsNullOrEmpty(orderNumber))
        {
            return;
        }

        bool released;
        lock (_processedOrdersLock)
        {
            // Confirmation wins if it raced this recovery path. Removing a confirmed key would
            // turn a successful print into a duplicate on the next poll.
            if (!_processedOrders.ContainsKey(orderNumber)
                || _persistableOrders.Contains(orderNumber))
            {
                return;
            }

            released = _processedOrders.Remove(orderNumber);
            // Keep a poll-delivered order's floor until a later retry confirms it. Without this,
            // the poll cursor advances beyond a failed/no-work order before the backend can assign
            // its route. SSE-only orders have no floor and are simply released from dedup.
            if (!_unconfirmedPollWindows.ContainsKey(orderNumber)
                && TryGetRetryFloor(createdAt, updatedAt, DateTime.UtcNow, out var retryFloor))
            {
                _unconfirmedPollWindows[orderNumber] = retryFloor;
            }
            _expiredUnconfirmed.Remove(orderNumber);
        }

        if (released)
        {
            _logger.LogInformation(
                "Order {OrderNumber} was not confirmed printed; releasing it for feed retry",
                orderNumber);
            // A polling delivery may have pinned the durable cursor to this order's window. Save
            // immediately while retaining that floor so a later route assignment is observable.
            PersistCursor(force: true);
        }
    }

    private static bool TryGetRetryFloor(
        DateTime? createdAt,
        DateTime? updatedAt,
        DateTime now,
        out DateTime floor)
    {
        var earliest = now - FeedCursorStore.MaxLookBack;
        var candidates = new[] { createdAt, updatedAt }
            .Where(value => value is { } timestamp && timestamp != default)
            .Select(value => value.GetValueOrDefault())
            .Select(timestamp => timestamp.Kind == DateTimeKind.Utc
                ? timestamp
                : timestamp.ToUniversalTime())
            .Where(timestamp => timestamp <= now)
            .ToList();
        if (candidates.Count == 0)
        {
            floor = default;
            return false;
        }

        var candidate = candidates.Min();
        floor = candidate <= earliest + RetryFloorAllowance
            ? earliest
            : candidate - RetryFloorAllowance;
        return true;
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
                PollingLogOperation,
                $"Order {orderNumber} may not have printed, and is too old to fetch again",
                "The order was received but never confirmed printed. Check the printer and reprint " +
                "it from the Orders tab if the ticket is missing.");
        }
    }
    /// <summary>
    /// POLLING-ONLY mechanism - polls at once, then every poll interval (5 seconds by default)
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
        var baseUrl = apiBaseUrl.TrimEnd('/');

        _logger.LogInformation("========================================");
        _logger.LogInformation("🔄 POLLING SERVICE STARTED");
        _logger.LogInformation("   API Base URL: {Url}", baseUrl);
        _logger.LogInformation("   Interval: {Interval} seconds", _pollInterval.TotalSeconds);
        _logger.LogInformation("========================================");
        System.Diagnostics.Debug.WriteLine(
            $"[POLLING] Started - URL: {baseUrl}, Interval: {_pollInterval.TotalSeconds}s");

        var pollCount = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (pollCount > 0)
                    await Task.Delay(_pollInterval, cancellationToken);

                pollCount++;
                DateTime pollWindowStart;
                string? updateCursor;
                lock (_processedOrdersLock)
                {
                    // A failed/no-work print releases its session dedup claim but keeps this
                    // per-order floor. Query from the oldest active floor so the same order can be
                    // observed again in this process, not only after a restart. Successful orders
                    // remove their own floor, so unrelated retries do not rewind the feed forever.
                    ExpireUnrecoverableUnconfirmedOrders();
                    pollWindowStart = _unconfirmedPollWindows.Values
                        .Append(_lastPollTime)
                        .Min();
                    updateCursor = _lastUpdateCursor;
                }

                var config = await _printerService.LoadConfigurationAsync();
                var language = config.PrintLanguage;
                _logger.LogInformation(
                    "🔄 Poll #{Count} - Fetching orders since {Since}", pollCount, pollWindowStart);
                OnConnectionStatusChanged($"Polling... (#{pollCount})");

                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                if (!string.IsNullOrWhiteSpace(config.ApiKey))
                    httpClient.DefaultRequestHeaders.Add("X-Api-Key", config.ApiKey);
                else
                    _logger.LogWarning("   ⚠️ No API key configured - request may fail if auth required");
                if (_deviceIdentity is not null && !string.IsNullOrWhiteSpace(_deviceIdentity.DeviceId))
                    httpClient.DefaultRequestHeaders.Add("X-Device-Id", _deviceIdentity.DeviceId);

                // The server's modifiedSince filter is a strict timestamp boundary. Capture it
                // immediately before the request so an order accepted while the response is in flight
                // remains eligible for the next poll. Commit this boundary only after every page drains.
                var requestStartedAt = DateTime.UtcNow;
                var page = await FetchFeedPageAsync(
                    httpClient, baseUrl, pollWindowStart, language, updateCursor, null, cancellationToken);
                if (page is null)
                    continue;

                var staged = true;
                var dedupChanged = false;
                if (!StageUpdates(page.Updates, ref dedupChanged))
                    staged = false;

                if (!staged)
                    continue;

                var currentPage = page;
                var orderCursor = (string?)null;
                var seenOrderCursors = new HashSet<string>(StringComparer.Ordinal);
                while (staged)
                {
                    foreach (var failure in currentPage.Errors)
                    {
                        // Update errors make IsSuccess false and return above. The remaining errors are
                        // legacy per-order diagnostics, which are safe to dedupe and persist as before.
                        if (!string.IsNullOrEmpty(failure.OrderNumber))
                        {
                            var errorKey = "error:" + failure.OrderNumber;
                            if (IsOrderAlreadyProcessed(errorKey))
                                continue;
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

                    _logger.LogInformation("   Orders found: {Count}", currentPage.Orders.Count);
                    foreach (var order in currentPage.Orders)
                    {
                        _logger.LogInformation(
                            "   Processing order: {OrderNumber} (Status: {Status})",
                            order.OrderNumber, order.Status);

                        if (IsOrderAlreadyProcessed(order.OrderNumber))
                        {
                            _logger.LogInformation("   ⏭️ Skipping duplicate: {OrderNumber}", order.OrderNumber);
                            continue;
                        }

                        // Orders are confirmed only by the pipeline after printing succeeds.
                        MarkOrderAsProcessed(order.OrderNumber, unconfirmedPollWindow: pollWindowStart);
                        OnOrderReceived(new OrderEvent
                        {
                            EventType = "order-polled",
                            Order = order,
                            Timestamp = DateTime.UtcNow,
                        });
                    }

                    if (!currentPage.HasMoreOrders)
                        break;

                    var nextOrderCursor = currentPage.NextOrderCursor;
                    if (string.IsNullOrWhiteSpace(nextOrderCursor)
                        || !seenOrderCursors.Add(nextOrderCursor))
                    {
                        LogPrinterFeedFailure(
                            "The order feed returned hasMoreOrders without a progressing cursor.");
                        staged = false;
                        break;
                    }

                    orderCursor = nextOrderCursor;
                    currentPage = await FetchFeedPageAsync(
                        httpClient,
                        baseUrl,
                        pollWindowStart,
                        language,
                        updateCursor,
                        orderCursor,
                        cancellationToken);
                    if (currentPage is null)
                    {
                        staged = false;
                        break;
                    }
                }

                // Drain every update page after all order pages. The backend uses a composite cursor
                // so equal-time notes cannot disappear behind a timestamp-only boundary.
                var nextUpdateCursor = page.NextUpdateCursor ?? updateCursor;
                var hasMoreUpdates = page.HasMoreUpdates;
                if (staged && !TryAdvanceUpdateCursor(nextUpdateCursor))
                    staged = false;

                var seenCursors = new HashSet<string>(StringComparer.Ordinal);
                if (!string.IsNullOrWhiteSpace(updateCursor))
                    seenCursors.Add(updateCursor);

                while (staged && hasMoreUpdates)
                {
                    if (string.IsNullOrWhiteSpace(nextUpdateCursor)
                        || !seenCursors.Add(nextUpdateCursor))
                    {
                        LogUpdateFeedFailure(
                            "The update feed returned hasMoreUpdates without a progressing cursor.");
                        staged = false;
                        break;
                    }

                    var nextPage = await FetchFeedPageAsync(
                        httpClient,
                        baseUrl,
                        pollWindowStart,
                        language,
                        nextUpdateCursor,
                        null,
                        cancellationToken);
                    if (nextPage is null)
                    {
                        staged = false;
                        break;
                    }

                    if (!StageUpdates(nextPage.Updates, ref dedupChanged))
                    {
                        staged = false;
                        break;
                    }

                    var pageCursor = nextPage.NextUpdateCursor ?? nextUpdateCursor;
                    if (!TryAdvanceUpdateCursor(pageCursor))
                    {
                        staged = false;
                        break;
                    }

                    nextUpdateCursor = pageCursor;
                    hasMoreUpdates = nextPage.HasMoreUpdates;
                }

                // Any failed page leaves the order cursor untouched. Update pages already accepted by
                // the update store remain safely durable and are idempotent on the next poll.
                if (!staged)
                    continue;

                var completedAt = DateTime.UtcNow;
                _lastSuccessfulPollAt = completedAt;

                // A changed order cursor is important state too; force it to disk rather than relying
                // on the one-minute debounce. The update cursor was already committed by its store.
                var cursorChanged = !string.Equals(updateCursor, nextUpdateCursor, StringComparison.Ordinal);
                if (_updateJobStore is not null)
                {
                    // The update store is authoritative. Its durable write succeeded page by page;
                    // this in-memory mirror may move ahead even if the legacy cursor mirror fails.
                    lock (_processedOrdersLock)
                    {
                        _lastUpdateCursor = nextUpdateCursor;
                    }
                }

                PersistCursor(
                    force: dedupChanged || cursorChanged,
                    proposedLastPollTime: requestStartedAt,
                    proposedUpdateCursor: nextUpdateCursor,
                    hasProposedUpdateCursor: true);

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
                _requestLogService.LogError(
                    PollingLogOperation, "Network error while polling for orders", httpEx.Message);
                OnConnectionStatusChanged($"Network error: {httpEx.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error during polling");
                _requestLogService.LogError(PollingLogOperation, "Error during polling", ex.Message);
                OnConnectionStatusChanged($"Error: {ex.Message}");
            }
        }

        _logger.LogInformation("🛑 Polling service stopped");
    }

    /// <summary>Fetches one successful page. A body-level success=false is a feed failure, not empty work.</summary>
    private async Task<OrderFeedParseResult?> FetchFeedPageAsync(
        HttpClient httpClient,
        string baseUrl,
        DateTime modifiedSince,
        string? language,
        string? updateCursor,
        string? orderCursor,
        CancellationToken cancellationToken)
    {
        var pollUrl = BuildFeedUrl(baseUrl, modifiedSince, language, updateCursor, orderCursor);
        _logger.LogInformation("Fetching printer feed page {Url}", pollUrl);
        using var response = await httpClient.GetAsync(pollUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var bodyPreview = errorBody[..Math.Min(200, errorBody.Length)];
            _logger.LogWarning("❌ Polling failed: {StatusCode} - {Body}", response.StatusCode, bodyPreview);
            var detail = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? "The API key is missing or incorrect. Enter the printer API key in Settings, then Save."
                : bodyPreview;
            _requestLogService.LogError(
                PollingLogOperation, $"Poll failed: {response.StatusCode}", detail);
            OnConnectionStatusChanged($"Poll failed: {response.StatusCode}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = OrderFeedParser.Parse(json);
        if (!result.IsSuccess || !result.HasDataEnvelope)
        {
            var detail = result.FailureMessage ?? "The backend rejected the printer feed response.";
            LogPrinterFeedFailure(detail);
            return null;
        }

        return result;
    }

    /// <summary>Builds the additive feed URL without losing the opaque update cursor.</summary>
    public static string BuildFeedUrl(
        string apiBaseUrl,
        DateTime modifiedSince,
        string? language,
        string? updateCursor = null,
        string? orderCursor = null)
    {
        var url = $"{apiBaseUrl.TrimEnd('/')}/api/orders/printer-feed?modifiedSince={Uri.EscapeDataString(modifiedSince.ToString("o", System.Globalization.CultureInfo.InvariantCulture))}&projectionVersion=2";
        if (!string.IsNullOrWhiteSpace(language))
            url += $"&language={Uri.EscapeDataString(language)}";
        if (!string.IsNullOrWhiteSpace(updateCursor))
            url += $"&updateCursor={Uri.EscapeDataString(updateCursor)}";
        if (!string.IsNullOrWhiteSpace(orderCursor))
            url += $"&orderCursor={Uri.EscapeDataString(orderCursor)}";
        return url;
    }

    /// <summary>
    /// Commits one update page position only after its jobs have been accepted by the update store.
    /// The legacy cursor store is used only by hosts that do not provide an update store.
    /// </summary>
    private bool TryAdvanceUpdateCursor(string? cursor)
    {
        if (_updateJobStore is not null)
        {
            if (!_updateJobStore.TryAdvanceUpdateCursor(cursor))
            {
                LogUpdateFeedFailure("Could not persist the update-feed cursor; the page will be retried.");
                return false;
            }

            // The store has accepted this cursor durably, so keeping the in-memory value ahead is
            // safe even if the legacy order-cursor mirror later fails.
            lock (_processedOrdersLock)
            {
                _lastUpdateCursor = cursor;
            }
        }

        return true;
    }

    /// <summary>Durably stages updates before notifying the asynchronous print pipeline.</summary>
    private bool StageUpdates(IEnumerable<PrinterFeedUpdate> updates, ref bool dedupChanged)
    {
        foreach (var update in updates)
        {
            if (_updateJobStore is null)
            {
                OnUpdateReceived(update);
                continue;
            }

            if (!_updateJobStore.AddOrGet(update, out _, out var shouldDispatch))
            {
                LogUpdateFeedFailure($"Could not durably stage update job {update.JobId}.");
                return false;
            }

            dedupChanged = true;
            if (shouldDispatch)
                OnUpdateReceived(update);
        }

        return true;
    }

    private void LogUpdateFeedFailure(string message)
    {
        _logger.LogError("Update feed failed: {Message}", message);
        _requestLogService.LogError(PollingLogOperation, "Update feed failed", message);
    }

    private void LogPrinterFeedFailure(string message)
    {
        _logger.LogError("Printer feed failed: {Message}", message);
        _requestLogService.LogError(PollingLogOperation, "Printer feed failed", message);
    }

    protected virtual void OnUpdateReceived(PrinterFeedUpdate update)
    {
        UpdateReceived?.Invoke(this, update);
    }
}
