namespace PrinterAPP.Services;

public enum PrinterUpdateAuthorizationStatus
{
    Authorized,
    Withdrawn,
    Unavailable,
}

public sealed record PrinterUpdateAuthorizationResult(PrinterUpdateAuthorizationStatus Status)
{
    public static PrinterUpdateAuthorizationResult Authorized { get; } =
        new(PrinterUpdateAuthorizationStatus.Authorized);
    public static PrinterUpdateAuthorizationResult Withdrawn { get; } =
        new(PrinterUpdateAuthorizationStatus.Withdrawn);
    public static PrinterUpdateAuthorizationResult Unavailable { get; } =
        new(PrinterUpdateAuthorizationStatus.Unavailable);
}
