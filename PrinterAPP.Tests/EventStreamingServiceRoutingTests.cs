using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public sealed class EventStreamingServiceRoutingTests
{
    [Fact]
    public async Task Sse_wrapper_payload_dispatches_named_route_enums()
    {
        var service = CreateService();
        var received = new List<OrderEvent>();
        service.OrderReceived += (_, orderEvent) => received.Add(orderEvent);

        await service.ProcessEventAsync("order-created", """
        {
          "eventType": "order-created",
          "timestamp": "2026-09-22T12:00:00Z",
          "order": {
            "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            "orderNumber": "SSE-ROUTE-1",
            "status": "Confirmed",
            "routingStates": [
              { "id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                "jobId": "cccccccc-cccc-cccc-cccc-cccccccccccc", "revision": 1,
                "target": "Cashier", "status": "Queued", "deviceId": "device-a" }
            ],
            "items": [ { "productName": "Pide", "quantity": 1 } ]
          }
        }
        """, "kitchen", CancellationToken.None);

        var dispatched = Assert.Single(received);
        var route = Assert.Single(dispatched.Order!.RoutingStates!);
        Assert.Equal(DevicePrintTarget.Cashier, route.Target);
        Assert.Equal(DevicePrintStatus.Queued, route.Status);
        Assert.Equal(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), route.JobId);
    }

    [Fact]
    public async Task Sse_direct_order_fallback_dispatches_route_payload()
    {
        var service = CreateService();
        var received = new List<OrderEvent>();
        service.OrderReceived += (_, orderEvent) => received.Add(orderEvent);

        await service.ProcessEventAsync("order-updated", """
        {
          "id": "dddddddd-dddd-dddd-dddd-dddddddddddd",
          "orderNumber": "SSE-ROUTE-2",
          "status": "Confirmed",
          "routingStates": [
            { "id": "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
              "jobId": "ffffffff-ffff-ffff-ffff-ffffffffffff", "revision": 2,
              "target": "General", "status": "Queued", "deviceId": "device-a" }
          ],
          "items": [ { "productName": "Soup", "quantity": 1 } ]
        }
        """, "kitchen", CancellationToken.None);

        var dispatched = Assert.Single(received);
        var route = Assert.Single(dispatched.Order!.RoutingStates!);
        Assert.Equal(DevicePrintTarget.General, route.Target);
        Assert.Equal(2, route.Revision);
    }

    [Fact]
    public async Task Sse_stream_dispatches_named_route_payload_from_the_http_response()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var service = CreateService();
        var received = new TaskCompletionSource<OrderEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.OrderReceived += (_, orderEvent) => received.TrySetResult(orderEvent);

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(cts.Token);
            using var stream = client.GetStream();
            await ReadHeadersAsync(stream, cts.Token);
            var body = "event: order-created\r\n" +
                       "data: {\"order\":{\"id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"orderNumber\":\"SSE-HTTP-1\",\"status\":\"Confirmed\",\"routingStates\":[{\"jobId\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\",\"revision\":1,\"target\":\"Cashier\",\"status\":\"Queued\",\"deviceId\":\"device-a\"}],\"items\":[{\"productName\":\"Pide\",\"quantity\":1}]}}\r\n\r\n";
            var payload = Encoding.UTF8.GetBytes(body);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, cts.Token);
            await stream.WriteAsync(payload, cts.Token);
            await stream.FlushAsync(cts.Token);
        }, cts.Token);

        var listenTask = service.ListenToStreamAsync($"http://127.0.0.1:{port}", "kitchen", cts.Token);
        var dispatched = await received.Task.WaitAsync(cts.Token);
        await cts.CancelAsync();
        await listenTask;
        await serverTask;

        var route = Assert.Single(dispatched.Order!.RoutingStates!);
        Assert.Equal(DevicePrintTarget.Cashier, route.Target);
        Assert.Equal(DevicePrintStatus.Queued, route.Status);
        Assert.Equal(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), route.JobId);
    }

    [Fact]
    public async Task Malformed_sse_route_is_diagnostic_and_never_dispatched()
    {
        var service = CreateService();
        var received = new List<OrderEvent>();
        service.OrderReceived += (_, orderEvent) => received.Add(orderEvent);

        await service.ProcessEventAsync("order-created", """
        {
          "order": {
            "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            "orderNumber": "SSE-BAD-1",
            "status": "Confirmed",
            "routingStates": [
              { "jobId": "00000000-0000-0000-0000-000000000000", "revision": 1,
                "target": "Cashier", "status": "Queued", "deviceId": "device-a" }
            ]
          }
        }
        """, "kitchen", CancellationToken.None);

        Assert.Empty(received);
    }

    private static EventStreamingService CreateService() => new(
        new StubPrinterService(), new NoopRequestLogService(), new InMemoryFeedCursorStore(),
        NullLogger<EventStreamingService>.Instance);

    private static async Task ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var headers = new StringBuilder();
        var buffer = new byte[1];
        while (!headers.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            headers.Append((char)buffer[0]);
        }
    }

    private sealed class StubPrinterService : IPrinterService
    {
        public string ConfigFilePath => "test";
        public Task<List<string>> GetAvailablePrintersAsync() => Task.FromResult(new List<string>());
        public Task<bool> PrintTestReceiptAsync(string printerName, PrinterConfiguration config) =>
            Task.FromResult(false);
        public Task<HttpStatusCode?> TestPrinterFeedAsync(string apiUrl, string? apiKey) =>
            Task.FromResult<HttpStatusCode?>(null);
        public Task<PrinterConfiguration> LoadConfigurationAsync() =>
            Task.FromResult(new PrinterConfiguration());
        public Task SaveConfigurationAsync(PrinterConfiguration config) => Task.CompletedTask;
    }

}
