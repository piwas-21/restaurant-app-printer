namespace PrinterAPP.Models;

/// <summary>Which logical printer a receipt was routed to. Names + values mirror the backend
/// <c>DevicePrintTarget</c> exactly (serialized as the name, e.g. "FrontKitchen").</summary>
public enum DevicePrintTarget
{
    Cashier = 1,
    FrontKitchen = 2,
    BackKitchen = 3,
    General = 4,
    Default = 5,
}
