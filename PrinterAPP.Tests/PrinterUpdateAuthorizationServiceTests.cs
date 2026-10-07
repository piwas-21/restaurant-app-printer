using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public sealed class PrinterUpdateAuthorizationServiceTests
{
    [Fact]
    public async Task Check_sends_exact_job_identity_and_accepts_matching_authority()
    {
        var update = PrinterUpdateTestData.Update(Guid.NewGuid(), target: DevicePrintTarget.FrontKitchen);
        using var handler = new AuthorizationHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Envelope(update, "Authorized")),
        });
        var service = new PrinterUpdateAuthorizationService(
            new HttpClient(handler),
            new ConfiguredPrinterService(new PrinterConfiguration
            {
                ApiBaseUrl = "https://tenant.example/",
                ApiKey = "test-key", // pragma: allowlist secret (fake test value)
            }),
            NullLogger<PrinterUpdateAuthorizationService>.Instance);

        var result = await service.CheckAsync(update);

        Assert.Equal(PrinterUpdateAuthorizationStatus.Authorized, result.Status);
        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal(
            $"https://tenant.example/api/printer-feed/updates/{update.JobId:D}/authorization?revision=1&target=FrontKitchen",
            handler.Request.RequestUri!.ToString());
        Assert.Equal("test-key", handler.Request.Headers.GetValues("X-Api-Key").Single()); // pragma: allowlist secret (fake test value)
        Assert.Null(handler.Request.Content);
    }

    [Theory]
    [InlineData("WrongJob", "Authorized")]
    [InlineData("WrongRevision", "Authorized")]
    [InlineData("WrongTarget", "Authorized")]
    [InlineData("Exact", "UnknownStatus")]
    public void Response_with_unmatched_identity_or_status_fails_closed(string identity, string status)
    {
        var update = PrinterUpdateTestData.Update(Guid.NewGuid(), target: DevicePrintTarget.FrontKitchen);
        var body = identity switch
        {
            "WrongJob" => Envelope(update with { JobId = Guid.NewGuid() }, status),
            "WrongRevision" => Envelope(update with { Revision = 2 }, status),
            "WrongTarget" => Envelope(update with { Target = DevicePrintTarget.BackKitchen }, status),
            _ => Envelope(update, status),
        };

        var result = PrinterUpdateAuthorizationService.ParseResponse(body, update);

        Assert.Equal(PrinterUpdateAuthorizationStatus.Unavailable, result.Status);
    }

    [Theory]
    [InlineData("\"1\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    public void Response_with_malformed_revision_json_fails_closed(string revisionJson)
    {
        var update = PrinterUpdateTestData.Update(Guid.NewGuid(), target: DevicePrintTarget.FrontKitchen);
        var body = "{\"success\":true,\"data\":{\"jobId\":\""
            + update.JobId.ToString("D")
            + "\",\"revision\":" + revisionJson
            + ",\"target\":\"FrontKitchen\",\"status\":\"Authorized\"}}";

        var result = PrinterUpdateAuthorizationService.ParseResponse(body, update);

        Assert.Equal(PrinterUpdateAuthorizationStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Withdrawal_status_is_explicit_and_revision_two_never_calls_authority()
    {
        var update = PrinterUpdateTestData.Update(Guid.NewGuid(), target: DevicePrintTarget.General);
        using var handler = new AuthorizationHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Envelope(update, "Withdrawn")),
        });
        var service = new PrinterUpdateAuthorizationService(
            new HttpClient(handler), new ConfiguredPrinterService(new PrinterConfiguration
            {
                ApiBaseUrl = "https://tenant.example",
                ApiKey = "test-key", // pragma: allowlist secret (fake test value)
            }), NullLogger<PrinterUpdateAuthorizationService>.Instance);

        Assert.Equal(PrinterUpdateAuthorizationStatus.Withdrawn,
            (await service.CheckAsync(update)).Status);
        Assert.Equal(PrinterUpdateAuthorizationStatus.Unavailable,
            (await service.CheckAsync(update with { Revision = 2, IsWithdrawn = true })).Status);
        Assert.Equal(1, handler.CallCount);
    }

    private static string Envelope(PrinterFeedUpdate update, string status) => JsonSerializer.Serialize(new
    {
        success = true,
        data = new
        {
            jobId = update.JobId,
            revision = update.Revision,
            target = update.Target.ToString(),
            status,
        },
    });

    private sealed class AuthorizationHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            Request = request;
            return Task.FromResult(response(request));
        }
    }

    private sealed class ConfiguredPrinterService(PrinterConfiguration configuration) : IPrinterService
    {
        public string ConfigFilePath => "test";
        public Task<List<string>> GetAvailablePrintersAsync() => Task.FromResult(new List<string>());
        public Task<bool> PrintTestReceiptAsync(string printerName, PrinterConfiguration config) =>
            Task.FromResult(false);
        public Task<HttpStatusCode?> TestPrinterFeedAsync(string apiUrl, string? apiKey) =>
            Task.FromResult<HttpStatusCode?>(null);
        public Task<PrinterConfiguration> LoadConfigurationAsync() => Task.FromResult(configuration);
        public Task SaveConfigurationAsync(PrinterConfiguration config) => Task.CompletedTask;
    }
}
