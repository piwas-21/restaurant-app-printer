namespace PrinterAPP.Tests;

/// <summary>
/// Sanitized synthetic printer-feed response for a two-meat Tacos menu.
/// The envelope and field names mirror the current backend PrinterFeedController,
/// OrderDto, OrderItemDto and OrderItemIngredientDto contract. IDs and order data
/// are fixture values; no live order or tenant identifiers are included.
/// </summary>
internal static class TacosMenuFeedFixture
{
    public const string Json = """
        {
          "success": true,
          "data": {
            "items": [
              {
                "id": "10000000-0000-0000-0000-000000000001",
                "orderNumber": "TEST-TACOS-FEED-0001",
                "userId": null,
                "customerName": null,
                "customerEmail": null,
                "customerPhone": null,
                "type": "TakeAway",
                "tableNumber": null,
                "tableId": null,
                "tableLabel": null,
                "serviceSessionId": null,
                "subTotal": 14.00,
                "tax": 0.00,
                "deliveryFee": 0.00,
                "discount": 0.00,
                "discountPercentage": 0.00,
                "customerDiscountAmount": 0.00,
                "fidelityPointsEarned": 0,
                "fidelityPointsRedeemed": 0,
                "fidelityPointsDiscount": 0.00,
                "tip": 0.00,
                "total": 14.00,
                "totalPaid": 0.00,
                "remainingAmount": 14.00,
                "isFullyPaid": false,
                "isKitchenReleased": false,
                "kitchenReleasedAt": null,
                "kitchenReleasedBy": null,
                "version": 1,
                "status": "Confirmed",
                "paymentStatus": "Pending",
                "isFocusOrder": false,
                "priority": null,
                "focusReason": null,
                "focusedAt": null,
                "focusedBy": null,
                "orderTypeOverrideBy": null,
                "orderTypeOverrideItems": null,
                "orderDate": "2026-09-27T10:00:00Z",
                "estimatedDeliveryTime": null,
                "actualDeliveryTime": null,
                "createdAt": "2026-09-27T10:00:00Z",
                "updatedAt": null,
                "preferredLanguage": null,
                "notes": null,
                "deliveryAddress": null,
                "cancellationReason": null,
                "promoCode": null,
                "hasUserLimitDiscount": false,
                "userLimitAmount": 0.00,
                "currency": "EUR",
                "items": [
                  {
                    "id": "20000000-0000-0000-0000-000000000001",
                    "productId": "30000000-0000-0000-0000-000000000001",
                    "productVariationId": null,
                    "menuID": null,
                    "productName": "Menu Tacos 2 Viande",
                    "variationName": null,
                    "quantity": 1,
                    "unitPrice": 14.00,
                    "itemTotal": 14.00,
                    "specialInstructions": null,
                    "kitchenType": "FrontKitchen",
                    "ingredientCustomizations": null,
                    "sideItems": [
                      {
                        "id": "20000000-0000-0000-0000-000000000002",
                        "productId": "30000000-0000-0000-0000-000000000002",
                        "productVariationId": null,
                        "menuID": null,
                        "productName": "Plat",
                        "variationName": null,
                        "quantity": 1,
                        "unitPrice": 0.00,
                        "itemTotal": 0.00,
                        "specialInstructions": null,
                        "kitchenType": "FrontKitchen",
                        "ingredientCustomizations": [
                          {
                            "ingredientId": "40000000-0000-0000-0000-000000000001",
                            "ingredientName": "Sauce Algérienne",
                            "quantity": 1,
                            "isRemoved": false,
                            "isAddOn": false
                          },
                          {
                            "ingredientId": "40000000-0000-0000-0000-000000000002",
                            "ingredientName": "Cheddar",
                            "quantity": 1,
                            "isRemoved": false,
                            "isAddOn": true
                          }
                        ],
                        "sideItems": null,
                        "kind": "BundleChild"
                      },
                      {
                        "id": "20000000-0000-0000-0000-000000000003",
                        "productId": "30000000-0000-0000-0000-000000000003",
                        "productVariationId": null,
                        "menuID": null,
                        "productName": "Poulet",
                        "variationName": null,
                        "quantity": 1,
                        "unitPrice": 0.00,
                        "itemTotal": 0.00,
                        "specialInstructions": null,
                        "kitchenType": "FrontKitchen",
                        "ingredientCustomizations": null,
                        "sideItems": null,
                        "kind": "BundleChild"
                      },
                      {
                        "id": "20000000-0000-0000-0000-000000000004",
                        "productId": "30000000-0000-0000-0000-000000000004",
                        "productVariationId": null,
                        "menuID": null,
                        "productName": "Kebab",
                        "variationName": null,
                        "quantity": 1,
                        "unitPrice": 0.00,
                        "itemTotal": 0.00,
                        "specialInstructions": null,
                        "kitchenType": "FrontKitchen",
                        "ingredientCustomizations": null,
                        "sideItems": null,
                        "kind": "BundleChild"
                      },
                      {
                        "id": "20000000-0000-0000-0000-000000000005",
                        "productId": "30000000-0000-0000-0000-000000000005",
                        "productVariationId": null,
                        "menuID": null,
                        "productName": "Frites",
                        "variationName": null,
                        "quantity": 1,
                        "unitPrice": 0.00,
                        "itemTotal": 0.00,
                        "specialInstructions": null,
                        "kitchenType": "FrontKitchen",
                        "ingredientCustomizations": null,
                        "sideItems": null,
                        "kind": "BundleChild"
                      },
                      {
                        "id": "20000000-0000-0000-0000-000000000006",
                        "productId": "30000000-0000-0000-0000-000000000006",
                        "productVariationId": null,
                        "menuID": null,
                        "productName": "Cola",
                        "variationName": null,
                        "quantity": 1,
                        "unitPrice": 0.00,
                        "itemTotal": 0.00,
                        "specialInstructions": null,
                        "kitchenType": "FrontKitchen",
                        "ingredientCustomizations": null,
                        "sideItems": null,
                        "kind": "BundleChild"
                      }
                    ],
                    "kind": null
                  }
                ],
                "payments": [],
                "statusHistory": []
              }
            ],
            "totalCount": 1,
            "page": 1,
            "pageSize": 50,
            "updates": [],
            "nextUpdateCursor": null,
            "hasMoreUpdates": false
          }
        }
        """;
}
