namespace PrinterAPP.Models;

/// <summary>
/// Mirrors the backend DeliveryAddressDto (Features/Orders/Dtos/DeliveryAddressDto.cs).
/// The printer feed serialises this as a JSON object, so the model MUST be an object too —
/// a plain string here made every delivery order throw DeserializeUnableToConvertValue and
/// wedged the whole poll batch (no orders printed until restart). GUIDs are strings to match
/// how the rest of the printer-app models carry backend IDs.
/// </summary>
public class DeliveryAddress
{
    public string Id { get; set; } = string.Empty;
    public string OrderId { get; set; } = string.Empty;
    public string? UserAddressId { get; set; }
    public string Label { get; set; } = string.Empty;
    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string? State { get; set; }
    public string PostalCode { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? DeliveryInstructions { get; set; }

    // Pre-formatted single-line address the backend builds for display/printing.
    public string FullAddress { get; set; } = string.Empty;
}
