using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public class TelemetryClientTests
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
        var client = new TelemetryClient(new HttpClient(handler), NullLogger<TelemetryClient>.Instance);
        return (client, handler);
    }

    private static HeartbeatRequest SampleRequest() => new()
    {
        Label = "Kitchen tablet",
        TenantSlug = "rumi",
        Platform = "Android",
        AppVersion = "1.0.20",
        FeedRunning = true,
        ApiBaseUrl = "https://api.example.com",
        KitchenPrinter = "192.168.1.50",
        SupportsUpdateAuthorization = true,
    };

    [Fact]
    public async Task SendHeartbeat_PostsToHeartbeatUrl_WithAuthAndDeviceHeaders()
    {
        var (client, handler) = Build();

        var ok = await client.SendHeartbeatAsync(
            SampleRequest(), "https://api.example.com/", "secret-key", "dev-123");

        Assert.True(ok);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://api.example.com/api/devices/heartbeat", handler.Request.RequestUri!.ToString());
        Assert.Equal("secret-key", handler.Request.Headers.GetValues("X-Api-Key").Single());
        Assert.Equal("dev-123", handler.Request.Headers.GetValues("X-Device-Id").Single());
    }

    [Fact]
    public async Task SendHeartbeat_BodyIsCamelCase_AndCarriesNoApiKey()
    {
        var (client, handler) = Build();

        await client.SendHeartbeatAsync(SampleRequest(), "https://api.example.com", "super-secret-key", "dev-123");

        Assert.NotNull(handler.Body);
        // The secret travels only as a header — it must never appear in the JSON body.
        Assert.DoesNotContain("super-secret-key", handler.Body);
        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.True(doc.RootElement.TryGetProperty("feedRunning", out _));   // camelCase
        Assert.True(doc.RootElement.GetProperty("supportsUpdateAuthorization").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("apiKey", out _));       // no key field at all
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public async Task SendHeartbeat_MapsStatusToBool(HttpStatusCode status, bool expected)
    {
        var (client, _) = Build(status);

        var result = await client.SendHeartbeatAsync(SampleRequest(), "https://api.example.com", "k", "dev-1");

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("", "dev-1")]
    [InlineData("https://api.example.com", "")]
    public async Task SendHeartbeat_MissingUrlOrDevice_ReturnsFalse_WithoutSending(string url, string deviceId)
    {
        var (client, handler) = Build();

        var result = await client.SendHeartbeatAsync(SampleRequest(), url, "k", deviceId);

        Assert.False(result);
        Assert.Null(handler.Request);   // nothing was sent
    }

    [Fact]
    public async Task SendHeartbeat_SwallowsTransportException_ReturnsFalse()
    {
        var (client, _) = Build(toThrow: new HttpRequestException("network down"));

        var result = await client.SendHeartbeatAsync(SampleRequest(), "https://api.example.com", "k", "dev-1");

        Assert.False(result);   // never throws — observability must not break the app
    }
}
