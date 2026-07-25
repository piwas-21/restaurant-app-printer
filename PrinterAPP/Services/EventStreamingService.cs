using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

public class EventStreamingService : IEventStreamingService
{
    private readonly IPrinterService _printerService;
    private readonly IRequestLogService _requestLogService;
    private readonly ILogger<EventStreamingService> _logger;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _kitchenListeningTask;
    private bool _isListening;

    // Track processed order IDs to prevent duplicate display/print (with timestamp for cleanup)
    private readonly Dictionary<string, DateTime> _processedOrders = new();
    private readonly object _processedOrdersLock = new();
    private const int MaxProcessedOrdersAge = 3600; // 1 hour in seconds
    private DateTime _lastPollTime = DateTime.UtcNow.AddMinutes(-30);  // Start 30 min ago to avoid fetching all historical orders
    private Task? _pollingTask;  // Primary polling mechanism

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
        ILogger<EventStreamingService> logger)
    {
        _printerService = printerService;
        _requestLogService = requestLogService;
        _logger = logger;
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
    private void MarkOrderAsProcessed(string orderNumber)
    {
        lock (_processedOrdersLock)
        {
            _processedOrders[orderNumber] = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Clean up processed orders older than MaxProcessedOrdersAge
    /// </summary>
    private void CleanupOldProcessedOrders()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-MaxProcessedOrdersAge);
        var oldOrders = _processedOrders
            .Where(kvp => kvp.Value < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var orderNumber in oldOrders)
        {
            _processedOrders.Remove(orderNumber);
        }

        if (oldOrders.Count > 0)
        {
            _logger.LogDebug("Cleaned up {Count} old processed order records", oldOrders.Count);
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
                var pollUrl = $"{baseUrl}/api/orders/printer-feed?modifiedSince={_lastPollTime:o}";

                _logger.LogInformation("🔄 Poll #{Count} - Fetching orders since {Since}", pollCount, _lastPollTime);
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
                    _requestLogService.LogError("Order Polling", $"Poll failed: {response.StatusCode}", detail);

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
                        MarkOrderAsProcessed(errorKey);
                    }

                    var who = !string.IsNullOrEmpty(failure.OrderNumber)
                        ? $"order {failure.OrderNumber}"
                        : failure.Index >= 0 ? $"order at index {failure.Index}" : "the feed response";
                    _logger.LogError("⚠️ Skipping un-deserialisable {Who}: {Message}", who, failure.Message);
                    _requestLogService.LogError(
                        "Order Polling",
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

                        MarkOrderAsProcessed(order.OrderNumber);

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

                _lastPollTime = DateTime.UtcNow;
                _lastSuccessfulPollAt = DateTime.UtcNow;
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
                _requestLogService.LogError("Order Polling", "Network error while polling for orders", httpEx.Message);
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
