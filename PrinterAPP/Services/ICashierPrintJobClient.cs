namespace PrinterAPP.Services;

public interface ICashierPrintJobClient
{
    Task PollAsync(CancellationToken cancellationToken);
}
