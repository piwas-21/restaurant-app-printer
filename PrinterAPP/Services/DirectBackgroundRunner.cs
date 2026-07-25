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

    public DirectBackgroundRunner(IOrderPipeline pipeline)
    {
        _pipeline = pipeline;
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        _pipeline.InitializeAsync(cancellationToken);
}
