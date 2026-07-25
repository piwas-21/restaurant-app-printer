namespace PrinterAPP.Services;

/// <summary>
/// Default <see cref="IBackgroundRunner"/> for heads that do not restrict background execution —
/// today that is Windows. A minimised desktop process is not frozen or reclaimed the way a
/// backgrounded Android process is, so there is no host service to start: initialising the pipeline
/// in-process is the whole job, and the behaviour is unchanged from before the pipeline was
/// extracted out of MainPage.
/// </summary>
public class DirectBackgroundRunner : IBackgroundRunner
{
    private readonly IOrderPipeline _pipeline;
    private readonly IFeedWatchdog _watchdog;

    public DirectBackgroundRunner(IOrderPipeline pipeline, IFeedWatchdog watchdog)
    {
        _pipeline = pipeline;
        _watchdog = watchdog;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Before InitializeAsync: the watchdog is idempotent, and starting it first means an init
        // failure is self-healing rather than terminal — its next tick starts the feed. Started here
        // rather than by the pipeline, which cannot own it without a dependency cycle.
        _watchdog.Start();

        await _pipeline.InitializeAsync(cancellationToken);
    }
}
