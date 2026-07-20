using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public class TelemetryClientPrintAcksTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly Exception? _throw;
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        public CapturingHandler(HttpStatusCode status = HttpStatusCode.OK, Exception? toThrow = null)
        {
            _status = status;
            _throw = toThrow;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            if (request.Content is not null)
                Body = await request.Content.ReadAsStringAsync(cancellationToken);
            if (_throw is not null)
                throw _throw;
            return new HttpResponseMessage(_status);
        }
    }

    private static (TelemetryClient client, CapturingHandler handler) Build(
        HttpStatusCode status = HttpStatusCode.OK, Exception? toThrow = null)
    {
        var handler = new CapturingHandler(status, toThrow);
        return (new TelemetryClient(new HttpClient(handler), NullLogger<TelemetryClient>.Instance), handler);
    }

    private static IReadOnlyList<PrintAck> SampleAcks() => new[]
    {
        new PrintAck { OrderId = Guid.NewGuid(), Target = DevicePrintTarget.FrontKitchen, Status = DevicePrintStatus.Printed, ReceivedAt = DateTime.UtcNow, PrintedAt = DateTime.UtcNow, Copies = 1 },
    };

    [Fact]
    public async Task SendPrintAcks_PostsBatch_WithHeaders_AndEnumNames()
    {
        var (client, handler) = Build();

        var result = await client.SendPrintAcksAsync(SampleAcks(), "https://api.example.com", "secret", "dev-1");

        Assert.Equal(TelemetrySendResult.Sent, result);
        Assert.Equal("https://api.example.com/api/devices/print-acks", handler.Request!.RequestUri!.ToString());
        Assert.Equal("secret", handler.Request.Headers.GetValues("X-Api-Key").Single());
        Assert.Equal("dev-1", handler.Request.Headers.GetValues("X-Device-Id").Single());
        using var doc = JsonDocument.Parse(handler.Body!);
        var ack = doc.RootElement.GetProperty("acks")[0];
        Assert.Equal("FrontKitchen", ack.GetProperty("target").GetString());   // enum as name
        Assert.Equal("Printed", ack.GetProperty("status").GetString());
        Assert.DoesNotContain("secret", handler.Body);   // key is a header, never in the body
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, TelemetrySendResult.Sent)]
    [InlineData(HttpStatusCode.BadRequest, TelemetrySendResult.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, TelemetrySendResult.Rejected)]
    [InlineData(HttpStatusCode.InternalServerError, TelemetrySendResult.Retry)]
    [InlineData(HttpStatusCode.TooManyRequests, TelemetrySendResult.Retry)]   // transient 4xx — keep
    [InlineData(HttpStatusCode.RequestTimeout, TelemetrySendResult.Retry)]    // transient 4xx — keep
    public async Task SendPrintAcks_MapsStatusToResult(HttpStatusCode status, TelemetrySendResult expected)
    {
        var (client, _) = Build(status);

        var result = await client.SendPrintAcksAsync(SampleAcks(), "https://api.example.com", "k", "dev-1");

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task SendPrintAcks_MissingConfig_ReturnsRetry_WithoutSending()
    {
        var (client, handler) = Build();

        var result = await client.SendPrintAcksAsync(SampleAcks(), "", "k", "dev-1");

        Assert.Equal(TelemetrySendResult.Retry, result);   // keep for later, don't drop
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task SendPrintAcks_TransportException_ReturnsRetry()
    {
        var (client, _) = Build(toThrow: new HttpRequestException("network down"));

        var result = await client.SendPrintAcksAsync(SampleAcks(), "https://api.example.com", "k", "dev-1");

        Assert.Equal(TelemetrySendResult.Retry, result);
    }
}
