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
        await _pipeline.InitializeAsync(cancellationToken);

        // Started here rather than by the pipeline: the watchdog depends on IOrderPipeline, so the
        // pipeline cannot own it without a dependency cycle. Idempotent.
        _watchdog.Start();
    }
}
