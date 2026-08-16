using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// Pins the container contract of <see cref="EventStreamingService"/>'s constructor.
///
/// <para><c>MauiProgram</c> registers the feed as
/// <c>AddSingleton&lt;IEventStreamingService, EventStreamingService&gt;()</c>, so the container — not
/// any code in this repo — calls the constructor. The poll interval is an <b>optional</b> parameter
/// with no registration behind it, which relies on Microsoft's container filling parameter defaults.
/// If that ever stopped holding, the failure would be an unresolvable service at app start: a crash
/// on the device, on a code path no PR job compiles. One second here buys that back.</para>
/// </summary>
public sealed class EventStreamingServiceDependencyInjectionTests
{
    [Fact]
    public void The_container_can_construct_the_feed_without_supplying_a_poll_interval()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPrinterService>(new StubPrinterService());
        services.AddSingleton<IRequestLogService>(new NoopRequestLogService());
        services.AddSingleton<IFeedCursorStore>(new InMemoryFeedCursorStore());
        services.AddSingleton<ILogger<EventStreamingService>>(
            NullLogger<EventStreamingService>.Instance);
        services.AddSingleton<IEventStreamingService, EventStreamingService>();

        using var provider = services.BuildServiceProvider(validateScopes: true);

        var feed = provider.GetRequiredService<IEventStreamingService>();

        Assert.IsType<EventStreamingService>(feed);
        Assert.False(feed.IsListening);
    }

    private sealed class StubPrinterService : IPrinterService
    {
        public string ConfigFilePath => "test-config.json";
        public Task<List<string>> GetAvailablePrintersAsync() => Task.FromResult(new List<string>());
        public Task<bool> PrintTestReceiptAsync(string printerName, Models.PrinterConfiguration config) =>
            Task.FromResult(true);
        public Task<System.Net.HttpStatusCode?> TestPrinterFeedAsync(string apiUrl, string? apiKey) =>
            Task.FromResult<System.Net.HttpStatusCode?>(System.Net.HttpStatusCode.OK);
        public Task<Models.PrinterConfiguration> LoadConfigurationAsync() =>
            Task.FromResult(new Models.PrinterConfiguration());
        public Task SaveConfigurationAsync(Models.PrinterConfiguration config) => Task.CompletedTask;
    }
}
