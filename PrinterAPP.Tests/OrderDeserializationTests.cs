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
          "items": [
            {
              "productName": "Pizza",
              "quantity": 1,
              "sideItems": [ { "productName": "Cola", "quantity": 2, "kind": "SideItem" } ]
            }
          ]
        }
        """;

        var order = JsonSerializer.Deserialize<Order>(json, Options);

        Assert.NotNull(order);
        Assert.Equal("fr", order!.PreferredLanguage);
        Assert.Equal("SideItem", order.Items[0].SideItems![0].Kind);
        Assert.Equal("WELCOME10", order.PromoCode);
        Assert.Equal(2.00m, order.Discount);
        Assert.Equal(10m, order.DiscountPercentage);
        Assert.Equal(1.50m, order.CustomerDiscountAmount);
        Assert.Equal(5.00m, order.TotalPaid);
        Assert.Equal(8.50m, order.RemainingAmount);
        Assert.False(order.IsFullyPaid);
    }

    /// <summary>
    /// Keeps the printer's mirror aligned with the current backend OrderDto rather than only
    /// testing the handful of fields the receipt currently reads. This fixture deliberately
    /// includes the additive staff/focus/release fields, nullable timestamps, permitted actions,
    /// payment reconciliation/refund fields, and the frozen ingredient IsAddOn flag. A missing or
    /// wrongly typed property either fails binding or leaves the asserted value at its default.
    /// Older payloads remain safe because all fields are additive and model collections initialize
    /// to empty lists.
    /// </summary>
    [Fact]
    public void Deserialize_current_backend_order_contract_without_dropping_fields()
    {
        const string json = """
        {
          "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
          "orderNumber": "202609170004",
          "userId": null,
          "customerName": null,
          "customerEmail": null,
          "customerPhone": null,
          "type": "DineIn",
          "tableNumber": 7,
          "tableId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
          "tableLabel": "Patio 7",
          "serviceSessionId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
          "subTotal": 22.00,
          "tax": 1.76,
          "deliveryFee": 0,
          "discount": 2.00,
          "discountPercentage": 10,
          "customerDiscountAmount": 1.00,
          "tip": 2.50,
          "total": 23.26,
          "totalPaid": 10.00,
          "remainingAmount": 13.26,
          "isFullyPaid": false,
          "isKitchenReleased": true,
          "kitchenReleasedAt": "2026-09-17T12:00:00Z",
          "kitchenReleasedBy": "staff-1",
          "version": 4,
          "status": "InProgress",
          "paymentStatus": "PartiallyPaid",
          "permittedActions": [
            { "action": "ReleaseKitchen", "allowed": true, "reasonCode": null, "requiresReason": false }
          ],
          "isFocusOrder": true,
          "priority": 2,
          "focusReason": "allergy",
          "focusedAt": "2026-09-17T12:01:00Z",
          "focusedBy": "staff-2",
          "orderTypeOverrideBy": "manager-1",
          "orderTypeOverrideItems": "no onion",
          "orderDate": "2026-09-17T11:55:00Z",
          "estimatedDeliveryTime": null,
          "actualDeliveryTime": null,
          "createdAt": null,
          "updatedAt": "2026-09-17T12:02:00Z",
          "preferredLanguage": "fr",
          "notes": "Call table",
          "deliveryAddress": null,
          "cancellationReason": null,
          "promoCode": "WELCOME10",
          "hasUserLimitDiscount": true,
          "userLimitAmount": 5.00,
          "currency": "EUR",
          "items": [
            {
              "id": "dddddddd-dddd-dddd-dddd-dddddddddddd",
              "productId": null,
              "productVariationId": "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
              "menuID": "ffffffff-ffff-ffff-ffff-ffffffffffff",
              "productName": "Tacos 1 Viande",
              "variationName": "Large",
              "quantity": 1,
              "unitPrice": 12.00,
              "itemTotal": 12.00,
              "specialInstructions": "Well done",
              "kitchenType": "FrontKitchen",
              "ingredientCustomizations": [
                {
                  "ingredientId": "11111111-1111-1111-1111-111111111111",
                  "ingredientName": "Cheddar",
                  "quantity": 1,
                  "isRemoved": false,
                  "isAddOn": true
                }
              ],
              "sideItems": null,
              "kind": null
            }
          ],
          "payments": [
            {
              "id": "12121212-1212-1212-1212-121212121212",
              "orderId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "operationId": "13131313-1313-1313-1313-131313131313",
              "paymentMethod": "Cash",
              "amount": 10.00,
              "status": "Completed",
              "transactionId": null,
              "referenceNumber": "CASH-1",
              "paymentDate": "2026-09-17T12:03:00Z",
              "cardLastFourDigits": null,
              "cardType": null,
              "paymentGateway": "manual",
              "paymentNotes": "Till 2",
              "isRefunded": true,
              "refundedAmount": 1.00,
              "refundDate": "2026-09-17T12:04:00Z",
              "createdAt": null,
              "refundReason": "Correction"
            }
          ],
          "statusHistory": [
            {
              "id": "14141414-1414-1414-1414-141414141414",
              "fromStatus": "Pending",
              "toStatus": "InProgress",
              "notes": null,
              "changedAt": "2026-09-17T12:00:00Z",
              "changedBy": "staff-1"
            }
          ]
        }
        """;

        var order = JsonSerializer.Deserialize<Order>(json, Options);

        Assert.NotNull(order);
        Assert.Equal("cccccccc-cccc-cccc-cccc-cccccccccccc", order!.ServiceSessionId.ToString());
        Assert.True(order.IsKitchenReleased);
        Assert.Equal(4, order.Version);
        Assert.True(order.IsFocusOrder);
        Assert.Equal(2, order.Priority);
        Assert.Equal("ReleaseKitchen", Assert.Single(order.PermittedActions!).Action);
        Assert.Equal("manager-1", order.OrderTypeOverrideBy);
        Assert.Null(order.CreatedAt);
        Assert.True(order.HasUserLimitDiscount);
        Assert.Equal(5.00m, order.UserLimitAmount);

        var ingredient = Assert.Single(Assert.Single(order.Items).IngredientCustomizations!);
        Assert.True(ingredient.IsAddOn);

        var payment = Assert.Single(order.Payments);
        Assert.Equal("13131313-1313-1313-1313-131313131313", payment.OperationId);
        Assert.Equal("manual", payment.PaymentGateway);
        Assert.True(payment.IsRefunded);
        Assert.Equal(1.00m, payment.RefundedAmount);
        Assert.Equal("Correction", payment.RefundReason);
        Assert.Null(payment.CreatedAt);
        Assert.Equal("InProgress", Assert.Single(order.StatusHistory).ToStatus);
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
