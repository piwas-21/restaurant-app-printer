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

    /// <summary>
    /// The fields the receipt reads beyond the classic order columns: the guest's language (drives
    /// the "order language" print option), a voucher code (explains a printed discount) and the
    /// customer-discount money that is separate from Discount (without it the printed breakdown
    /// cannot reconcile against the total). Each mirrors its backend OrderDto field exactly.
    /// </summary>
    [Fact]
    public void Deserialize_binds_language_promo_and_customer_discount_fields()
    {
        const string json = """
        {
          "orderNumber": "202609040003",
          "type": "TakeAway",
          "preferredLanguage": "fr",
          "promoCode": "WELCOME10",
          "discount": 2.00,
          "discountPercentage": 10,
          "customerDiscountAmount": 1.50,
          "totalPaid": 5.00,
          "remainingAmount": 8.50,
          "isFullyPaid": false,
          "items": []
        }
        """;

        var order = JsonSerializer.Deserialize<Order>(json, Options);

        Assert.NotNull(order);
        Assert.Equal("fr", order!.PreferredLanguage);
        Assert.Equal("WELCOME10", order.PromoCode);
        Assert.Equal(2.00m, order.Discount);
        Assert.Equal(10m, order.DiscountPercentage);
        Assert.Equal(1.50m, order.CustomerDiscountAmount);
        Assert.Equal(5.00m, order.TotalPaid);
        Assert.Equal(8.50m, order.RemainingAmount);
        Assert.False(order.IsFullyPaid);
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
