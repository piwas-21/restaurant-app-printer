using PrinterAPP.Models;

namespace PrinterAPP.Services;

public interface IOrderPrintService
{
    Task<(bool Cashier, bool FrontKitchen, bool BackKitchen)> PrintOrderToAllPrintersAsync(
        Order order,
        bool isManualPrint = false,
        CancellationToken cancellationToken = default);

    Task<bool> PrintOrderAsync(
        Order order,
        OrderPrintService.PrinterType printerType,
        bool isManualPrint = false,
        CancellationToken cancellationToken = default);
}
