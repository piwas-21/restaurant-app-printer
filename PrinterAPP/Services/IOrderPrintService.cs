using PrinterAPP.Models;

namespace PrinterAPP.Services;

public interface IOrderPrintService
{
    /// <summary>
    /// Prints to every destination the order owes work: always the cashier receipt, then the
    /// kitchen tickets the routing policy asks for.
    /// <para>SIGNATURE CHANGE (issue #113 S2): the two kitchen <c>bool</c> tuple elements became
    /// <see cref="KitchenPrintOutcome"/> and a fourth element, <c>GeneralDefault</c>, carries the
    /// General (SingleKitchen) / Default (Stations) destination outcome. Implicit
    /// <c>bool</c> conversions keep boolean reads source-compatible (true ⇔ sent). Only
    /// <c>GeneralDefault</c> can be <see cref="KitchenPrintStatus.NotConfigured"/>: work exists for
    /// it but no printer resolves, which is neither printed nor success — it used to collapse into
    /// a silent <c>true</c>.</para>
    /// </summary>
    Task<(bool Cashier, KitchenPrintOutcome FrontKitchen, KitchenPrintOutcome BackKitchen, KitchenPrintOutcome GeneralDefault)>
        PrintOrderToAllPrintersAsync(
            Order order,
            bool isManualPrint = false,
            CancellationToken cancellationToken = default);

    Task<bool> PrintOrderAsync(
        Order order,
        PrinterType printerType,
        bool isManualPrint = false,
        CancellationToken cancellationToken = default);

    /// <summary>Prints one additive UPDATE job to its resolved General/Default destination.</summary>
    Task<KitchenPrintOutcome> PrintUpdateAsync(
        PrinterFeedUpdate update,
        Func<CancellationToken, Task<PrinterUpdateAuthorizationResult>> authorizeImmediatelyBeforeSend,
        CancellationToken cancellationToken = default);
}
