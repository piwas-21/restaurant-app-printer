using System.Text.Json;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>
/// Resilient parser for the printer-feed response body (<c>{ "data": { "items": [ ...orders ] } }</c>).
///
/// Deserialises every order in <c>data.items</c> INDEPENDENTLY so one malformed order cannot wedge the
/// whole feed. A prod bug (fixed in PR #61 / v1.0.20) shipped because a single field drift
/// (<c>deliveryAddress</c> typed <c>string</c> vs the object the backend sends) threw for the entire
/// batch inside the poll loop; because <c>_lastPollTime</c> only advances after a successful batch, the
/// next 5s poll re-fetched and re-threw on the same order forever — nothing printed until app restart.
///
/// Here each element is deserialised in its own try/catch: good orders come back in
/// <see cref="OrderFeedParseResult.Orders"/>, and each failure is captured in
/// <see cref="OrderFeedParseResult.Errors"/> (with the order number/index when extractable) for the
/// caller to log to the Errors/Diagnostics page and skip. MAUI-free so the unit-test project
/// (plain net10.0) can link it — same pattern as <see cref="PrinterTransportResolver"/>.
/// </summary>
public static class OrderFeedParser
{
    // Same options the poll/SSE paths used for the whole-batch deserialize: case-insensitive matching so
    // the backend's camelCase JSON binds to the models' PascalCase properties.
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Parses a printer-feed response body. Never throws for a malformed order or a malformed envelope:
    /// envelope/parse problems and per-order failures are returned in
    /// <see cref="OrderFeedParseResult.Errors"/>. An absent or empty <c>data.items</c> array yields an
    /// empty result with no errors (matches the old "no new orders" path).
    /// </summary>
    public static OrderFeedParseResult Parse(string json)
    {
        var orders = new List<Order>();
        var errors = new List<OrderFeedParseError>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        // Broad by design: the whole point of this parser is that NOTHING about a bad response body
        // escapes to wedge the poll loop. A malformed body (JsonException) or a null one
        // (ArgumentNullException) becomes a reported envelope error, never a thrown exception.
        catch (Exception ex)
        {
            errors.Add(new OrderFeedParseError(-1, null, $"Response body was not valid JSON: {ex.Message}"));
            return new OrderFeedParseResult(orders, errors);
        }

        using (document)
        {
            // data.items is the paged wrapper; a missing/empty array is a normal "no orders" poll.
            if (!TryGetPropertyIgnoreCase(document.RootElement, "data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !TryGetPropertyIgnoreCase(data, "items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return new OrderFeedParseResult(orders, errors);
            }

            var index = 0;
            foreach (var element in items.EnumerateArray())
            {
                try
                {
                    var order = element.Deserialize<Order>(Options);
                    if (order is null)
                    {
                        errors.Add(new OrderFeedParseError(index, TryReadOrderNumber(element),
                            "Order element deserialised to null."));
                    }
                    else
                    {
                        orders.Add(order);
                    }
                }
                // Broad by design: one un-deserialisable order — for ANY reason, not just JsonException
                // (a future model change could make System.Text.Json throw NotSupportedException etc.) —
                // is logged + skipped by the caller; the rest of the batch still prints. This is the whole
                // guarantee of the fix, so it must not be scoped to a single exception type.
                catch (Exception ex)
                {
                    errors.Add(new OrderFeedParseError(index, TryReadOrderNumber(element), ex.Message));
                }

                index++;
            }
        }

        return new OrderFeedParseResult(orders, errors);
    }

    // Best-effort order number for diagnostics — read straight off the raw element so it works even when
    // the element as a whole fails to deserialise (that's exactly when the caller needs to name the order).
    private static string? TryReadOrderNumber(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object
            && TryGetPropertyIgnoreCase(element, "orderNumber", out var orderNumber))
        {
            // Only String/Number are real order numbers. ToString() on Null/Bool/Object/Array would emit
            // misleading raw JSON ("null", "true", "{}") into diagnostics, so treat those as "no number".
            return orderNumber.ValueKind switch
            {
                JsonValueKind.String => orderNumber.GetString(),
                JsonValueKind.Number => orderNumber.ToString(),
                _ => null
            };
        }

        return null;
    }

    // JsonElement.TryGetProperty is case-sensitive; the old whole-batch path bound case-insensitively
    // (PropertyNameCaseInsensitive), so mirror that here to avoid a silent behaviour change on casing.
    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}

/// <summary>Outcome of <see cref="OrderFeedParser.Parse"/>: the orders that deserialised, and the failures.</summary>
public sealed record OrderFeedParseResult(IReadOnlyList<Order> Orders, IReadOnlyList<OrderFeedParseError> Errors);

/// <summary>
/// A single order (or the envelope) that could not be read from the feed. <see cref="Index"/> is the
/// position in <c>data.items</c> (or -1 for an envelope/whole-body failure); <see cref="OrderNumber"/> is
/// the order number when it could be read off the raw JSON, otherwise null.
/// </summary>
public sealed record OrderFeedParseError(int Index, string? OrderNumber, string Message);
