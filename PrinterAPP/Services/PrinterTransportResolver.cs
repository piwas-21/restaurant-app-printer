namespace PrinterAPP.Services;

/// <summary>
/// Resolves one configured printer-name field (e.g. <c>PrinterConfiguration.CashierPrinterName</c>)
/// to the <see cref="IPrinterTransport"/> that carries the order's ESC/POS bytes, encoding the
/// order-print path's historical routing rule: a literal IP address (<c>"ip"</c> or
/// <c>"ip:port"</c>, per <see cref="PrinterEndpoint.TryParse"/>) is a network printer
/// (<see cref="NetworkTcpTransport"/>); anything else is a Windows spooler printer name used
/// verbatim (<see cref="WindowsSpoolerTransport"/> — which throws <see cref="ArgumentException"/>
/// for blank names; callers guard, and the caller's catch maps it to the same print-failure result
/// the legacy inline P/Invoke produced). Extracted from <c>OrderPrintService</c> so the routing is
/// unit-testable without MAUI, same as <see cref="PrinterTestTarget"/> for the test-print path.
/// ADR-006 Phase 2b. Transports are constructed per target (a per-printer value), not DI singletons.
/// </summary>
public static class PrinterTransportResolver
{
    public static IPrinterTransport Resolve(string printerName) =>
        PrinterEndpoint.TryParse(printerName, out var ip, out var port)
            ? new NetworkTcpTransport(ip, port)
            : new WindowsSpoolerTransport(printerName);
}
