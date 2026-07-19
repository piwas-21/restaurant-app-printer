using System.Text.Json;
using PrinterAPP.Models;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// Pins the shape of the printer-feed JSON against the <see cref="Order"/> model. A prod bug shipped
/// because <c>Order.DeliveryAddress</c> was typed <c>string?</c> while the backend serialises
/// deliveryAddress as an object: every delivery order threw DeserializeUnableToConvertValue in the
/// poll loop, and because the whole batch throws the feed re-threw on the same order every 5s and
/// never printed anything new. These tests fail fast if that drift ever returns.
/// </summary>
public class OrderDeserializationTests
{
    // Same options the polling/SSE paths use (EventStreamingService): backend camelCase -> PascalCase.
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void Deserialize_delivery_order_binds_the_address_object()
    {
        const string json = """
        {
          "id": "8f14e45f-ceea-467e-9c3a-1a2b3c4d5e6f",
          "orderNumber": "202607190012",
          "type": "Delivery",
          "status": "Confirmed",
          "deliveryAddress": {
            "id": "11111111-1111-1111-1111-111111111111",
            "orderId": "8f14e45f-ceea-467e-9c3a-1a2b3c4d5e6f",
            "userAddressId": null,
            "label": "Home",
            "addressLine1": "Rue du Rhone 12",
            "addressLine2": "Apt 4",
            "city": "Geneve",
            "state": null,
            "postalCode": "1204",
            "country": "CH",
            "phone": "+41 79 000 0000",
            "latitude": 46.2044,
            "longitude": 6.1432,
            "deliveryInstructions": "Ring twice",
            "fullAddress": "Rue du Rhone 12, Apt 4, 1204 Geneve, CH"
          },
          "items": [ { "productName": "Lahmacun", "quantity": 2, "kitchenType": "FrontKitchen" } ]
        }
        """;

        var order = JsonSerializer.Deserialize<Order>(json, Options);

        Assert.NotNull(order);
        Assert.NotNull(order!.DeliveryAddress);
        Assert.Equal("Rue du Rhone 12, Apt 4, 1204 Geneve, CH", order.DeliveryAddress!.FullAddress);
        Assert.Equal("Geneve", order.DeliveryAddress.City);
        Assert.Equal("1204", order.DeliveryAddress.PostalCode);
        Assert.Equal(46.2044, order.DeliveryAddress.Latitude);
        Assert.Null(order.DeliveryAddress.UserAddressId);
    }

    [Fact]
    public void Deserialize_non_delivery_order_leaves_address_null()
    {
        const string json = """
        { "orderNumber": "202607190013", "type": "TakeAway", "deliveryAddress": null, "items": [] }
        """;

        var order = JsonSerializer.Deserialize<Order>(json, Options);

        Assert.NotNull(order);
        Assert.Null(order!.DeliveryAddress);
    }
}
