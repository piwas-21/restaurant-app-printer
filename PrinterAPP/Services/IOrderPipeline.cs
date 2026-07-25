using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>
/// Owns the order path end to end — feed → print → history → print-ack — with no dependency on a
/// page, window or Activity.
/// <para>Before this existed the whole path was wired up in <c>MainPage</c>'s constructor and ran
/// inside <c>MainThread.BeginInvokeOnMainThread</c>, so it only worked while the app was the
/// foreground UI. On Android that is exactly why orders stopped printing when staff minimised the
/// app: the process was throttled by Doze/App Standby and then reclaimed by the low-memory killer.
/// The Android foreground service that now keeps the app alive — and the copy started after a
/// reboot — has no Activity at all, so this layer must be headless.
/// See docs/adr/ADR-007-android-foreground-service.md.</para>
/// </summary>
public interface IOrderPipeline
{
    /// <summary>True while the order feed is listening for orders.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Raised after each order finishes printing so a UI can show the outcome. Raised on a
    /// background thread — subscribers must marshal to the UI thread themselves.
    /// </summary>
    event EventHandler<OrderProcessedEventArgs>? OrderProcessed;

    /// <summary>
    /// Brings up the always-on parts — Sentry identity tags and the fleet heartbeat — and starts the
    /// order feed if the saved configuration says it should be running. Idempotent; called whenever
    /// a host comes up (app launch, foreground-service start, post-reboot start).
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts the order feed. Idempotent.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the order feed. The telemetry heartbeat deliberately keeps running: a device whose feed
    /// is stopped or wedged must stay visible on the fleet dashboard — that was the blind spot in the
    /// 2026-07-19 incident.
    /// </summary>
    Task StopAsync();
}

/// <summary>Outcome of one order's trip through the pipeline, for UI status only.</summary>
public class OrderProcessedEventArgs : EventArgs
{
    public required Order Order { get; init; }
    public required bool Cashier { get; init; }
    public required bool FrontKitchen { get; init; }
    public required bool BackKitchen { get; init; }

    /// <summary>Set when the order failed to print; null when it reached the printers.</summary>
    public Exception? Error { get; init; }

    /// <summary>
    /// True only when every target reported success. A false flag is a genuine print failure:
    /// <c>OrderPrintService</c> already returns true for the benign cases (no printer configured for
    /// that target, auto-print off, no items routed to that kitchen, outside the print window).
    /// Worth surfacing separately from <see cref="Error"/> — a partial print means a kitchen ticket
    /// never came out while the cashier receipt did, which is silent unless someone is told.
    /// </summary>
    public bool AllPrinted => Cashier && FrontKitchen && BackKitchen;
}
