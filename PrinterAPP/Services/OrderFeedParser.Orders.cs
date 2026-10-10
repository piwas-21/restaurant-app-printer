using System.Text.Json;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

public static partial class OrderFeedParser
{
    private static void ParseOrdersIfPresent(JsonElement data, ParsedData parsed)
    {
        if (TryGetPropertyIgnoreCase(data, "items", out var items) && items.ValueKind == JsonValueKind.Array)
            ParseOrders(items, parsed);
    }

    private static void ParseOrders(JsonElement items, ParsedData parsed)
    {
        var index = 0;
        foreach (var element in items.EnumerateArray())
        {
            try
            {
                var order = element.Deserialize<Order>(PrinterJsonSerialization.Options);
                if (order is null)
                    parsed.Errors.Add(new OrderFeedParseError(index, TryReadOrderNumber(element), "Order element deserialised to null."));
                else if (!OrderRoutingStateValidation.TryValidate(order, out var routeError))
                {
                    parsed.Errors.Add(new OrderFeedParseError(index, order.OrderNumber,
                        routeError ?? "Invalid printer routing state."));
                    parsed.Fail($"Malformed printer routing state for order {order.OrderNumber}: {routeError}");
                }
                else
                {
                    if (parsed.ProjectionVersion >= 2)
                        OrderPresentationMetadataNormalizer.Normalize(order);
                    parsed.Orders.Add(order);
                }
            }
            catch (Exception ex)
            {
                parsed.Errors.Add(new OrderFeedParseError(index, TryReadOrderNumber(element), ex.Message));
                if (HasNonEmptyRoutingStates(element))
                    parsed.Fail($"Malformed printer routing state: {ex.Message}");
            }
            index++;
        }
    }

    private static bool HasNonEmptyRoutingStates(JsonElement element) =>
        TryGetPropertyIgnoreCase(element, "routingStates", out var routes)
        && routes.ValueKind == JsonValueKind.Array
        && routes.GetArrayLength() > 0;
}
