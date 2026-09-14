using System.Text.Json;
using System.Text.Json.Serialization;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>Resilient parser for orders and additive update jobs in one printer-feed response.</summary>
public static class OrderFeedParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
    /// <summary>Parses a feed body without throwing for malformed JSON or individual entries.</summary>
    public static OrderFeedParseResult Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return ParseRoot(document.RootElement);
        }
        catch (Exception ex)
        {
            return InvalidBody(ex);
        }
    }
    private static OrderFeedParseResult ParseRoot(JsonElement root)
    {
        var status = ReadSuccessStatus(root);
        if (!TryGetPropertyIgnoreCase(root, "data", out var data) || data.ValueKind != JsonValueKind.Object)
            return InvalidEnvelope(status.FailureMessage);
        var parsed = ParseData(data);
        ApplyEnvelopeStatus(parsed, status);
        return ToResult(parsed);
    }
    private static FeedStatus ReadSuccessStatus(JsonElement root)
    {
        if (!TryGetPropertyIgnoreCase(root, "success", out var success))
            return new(true, null);
        return success.ValueKind switch
        {
            JsonValueKind.True => new(true, null),
            JsonValueKind.False => new(false, ReadMessage(root)),
            _ => new(false, "The feed success flag was not a boolean."),
        };
    }
    private static string? ReadMessage(JsonElement root) =>
        TryGetPropertyIgnoreCase(root, "message", out var message) && message.ValueKind == JsonValueKind.String
            ? message.GetString() : null;
    private static ParsedData ParseData(JsonElement data)
    {
        var parsed = new ParsedData();
        ParseOrdersIfPresent(data, parsed);
        ReadNextUpdateCursor(data, parsed);
        ReadHasMoreUpdates(data, parsed);
        ParseUpdatesIfPresent(data, parsed);
        ValidateUpdatePage(parsed);
        return parsed;
    }
    private static void ParseOrdersIfPresent(JsonElement data, ParsedData parsed)
    {
        if (TryGetPropertyIgnoreCase(data, "items", out var items) && items.ValueKind == JsonValueKind.Array)
            ParseOrders(items, parsed.Orders, parsed.Errors);
    }
    private static void ReadNextUpdateCursor(JsonElement data, ParsedData parsed)
    {
        if (!TryGetPropertyIgnoreCase(data, "nextUpdateCursor", out var cursor))
            return;
        if (cursor.ValueKind == JsonValueKind.String)
        {
            parsed.NextUpdateCursor = cursor.GetString();
            if (string.IsNullOrWhiteSpace(parsed.NextUpdateCursor))
                parsed.Fail("The update cursor was empty.");
            return;
        }
        if (cursor.ValueKind != JsonValueKind.Null)
            parsed.Fail("The update cursor was not a string or null.");
    }
    private static void ReadHasMoreUpdates(JsonElement data, ParsedData parsed)
    {
        if (!TryGetPropertyIgnoreCase(data, "hasMoreUpdates", out var more))
            return;
        if (more.ValueKind == JsonValueKind.True)
        {
            parsed.HasMoreUpdates = true;
            return;
        }
        if (more.ValueKind != JsonValueKind.False)
            parsed.Fail("The update page flag was not a boolean.");
    }
    private static void ParseUpdatesIfPresent(JsonElement data, ParsedData parsed)
    {
        if (!TryGetPropertyIgnoreCase(data, "updates", out var updateArray))
            return;
        if (updateArray.ValueKind == JsonValueKind.Array)
        {
            ParseUpdates(updateArray, parsed.Updates, parsed.UpdateErrors);
            return;
        }
        parsed.Fail("The update page was not an array.");
    }
    private static void ValidateUpdatePage(ParsedData parsed)
    {
        if ((parsed.Updates.Count > 0 || parsed.HasMoreUpdates)
            && string.IsNullOrWhiteSpace(parsed.NextUpdateCursor))
            parsed.Fail("An update page requires a non-empty next cursor.");
    }
    private static void ApplyEnvelopeStatus(ParsedData parsed, FeedStatus status)
    {
        if (!status.IsSuccess)
        {
            parsed.IsSuccess = false;
            parsed.FailureMessage = status.FailureMessage ?? parsed.FailureMessage;
        }
        if (parsed.UpdateErrors.Count == 0)
            return;
        parsed.IsSuccess = false;
        parsed.FailureMessage ??= $"The update page contained {parsed.UpdateErrors.Count} invalid item(s).";
        parsed.Errors.AddRange(parsed.UpdateErrors);
    }
    private static OrderFeedParseResult ToResult(ParsedData parsed) => new(parsed.Orders, parsed.Errors)
    {
        Updates = parsed.Updates, UpdateErrors = parsed.UpdateErrors, NextUpdateCursor = parsed.NextUpdateCursor,
        HasMoreUpdates = parsed.HasMoreUpdates, IsSuccess = parsed.IsSuccess, HasDataEnvelope = true,
        FailureMessage = parsed.FailureMessage,
    };
    private static OrderFeedParseResult InvalidBody(Exception ex) => new(
        Array.Empty<Order>(), new[] { new OrderFeedParseError(-1, null, $"Response body was not valid JSON: {ex.Message}") })
    {
        IsSuccess = false, HasDataEnvelope = false,
    };
    private static OrderFeedParseResult InvalidEnvelope(string? failureMessage) => new(
        Array.Empty<Order>(), Array.Empty<OrderFeedParseError>())
    {
        IsSuccess = false, HasDataEnvelope = false, FailureMessage = failureMessage,
    };
    private static void ParseOrders(JsonElement items, ICollection<Order> orders, ICollection<OrderFeedParseError> errors)
    {
        var index = 0;
        foreach (var element in items.EnumerateArray())
        {
            try
            {
                var order = element.Deserialize<Order>(Options);
                if (order is null)
                    errors.Add(new OrderFeedParseError(index, TryReadOrderNumber(element), "Order element deserialised to null."));
                else
                    orders.Add(order);
            }
            catch (Exception ex)
            {
                errors.Add(new OrderFeedParseError(index, TryReadOrderNumber(element), ex.Message));
            }
            index++;
        }
    }
    private static void ParseUpdates(JsonElement updateArray, ICollection<PrinterFeedUpdate> updates,
        ICollection<OrderFeedParseError> errors)
    {
        var index = 0;
        foreach (var element in updateArray.EnumerateArray())
        {
            try
            {
                var update = element.Deserialize<PrinterFeedUpdate>(Options);
                if (update is null)
                    errors.Add(new OrderFeedParseError(index, TryReadUpdateOrderNumber(element), "Update element deserialised to null."));
                else
                {
                    var validationError = ValidateUpdate(element, update);
                    if (validationError is not null)
                        errors.Add(new OrderFeedParseError(index, TryReadUpdateOrderNumber(element), validationError));
                    else
                        updates.Add(update);
                }
            }
            catch (Exception ex)
            {
                errors.Add(new OrderFeedParseError(index, TryReadUpdateOrderNumber(element), ex.Message));
            }
            index++;
        }
    }
    private static string? ValidateUpdate(JsonElement element, PrinterFeedUpdate update)
    {
        foreach (var property in RequiredUpdateProperties)
        {
            if (!TryGetPropertyIgnoreCase(element, property, out _))
                return $"Update {property} is required.";
        }
        if (update.JobId == Guid.Empty) return "Update jobId is required.";
        if (update.Revision <= 0) return "Update revision must be positive.";
        if (update.JobType != DevicePrintJobType.Update) return "Update jobType is not Update.";
        if (update.Target is not (DevicePrintTarget.General or DevicePrintTarget.Default))
            return "Update target is not General or Default.";
        if (update.OrderId == Guid.Empty) return "Update orderId is required.";
        if (!string.Equals(update.Audience, "Kitchen", StringComparison.OrdinalIgnoreCase))
            return "Update audience is not Kitchen.";
        if (string.IsNullOrWhiteSpace(update.OrderNumber)) return "Update orderNumber is required.";
        if (string.IsNullOrWhiteSpace(update.Text)) return "Update text is required.";
        if (update.CreatedAt == default) return "Update createdAt is required.";
        return null;
    }
    private static readonly string[] RequiredUpdateProperties = [
        "jobId", "revision", "jobType", "target", "orderId", "orderNumber", "audience", "text", "createdAt"];
    private static string? TryReadOrderNumber(JsonElement element) => TryReadScalar(element, "orderNumber");
    private static string? TryReadUpdateOrderNumber(JsonElement element) => TryReadScalar(element, "orderNumber");
    private static string? TryReadScalar(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !TryGetPropertyIgnoreCase(element, propertyName, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.ToString(), _ => null,
        };
    }
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
    private sealed record FeedStatus(bool IsSuccess, string? FailureMessage);
    private sealed class ParsedData
    {
        public List<Order> Orders { get; } = [];
        public List<PrinterFeedUpdate> Updates { get; } = [];
        public List<OrderFeedParseError> Errors { get; } = [];
        public List<OrderFeedParseError> UpdateErrors { get; } = [];
        public string? NextUpdateCursor { get; set; }
        public bool HasMoreUpdates { get; set; }
        public bool IsSuccess { get; set; } = true;
        public string? FailureMessage { get; set; }
        public void Fail(string message) { IsSuccess = false; FailureMessage ??= message; }
    }
}
/// <summary>Orders, updates, cursor metadata and parse diagnostics from one feed response.</summary>
public sealed record OrderFeedParseResult(IReadOnlyList<Order> Orders, IReadOnlyList<OrderFeedParseError> Errors)
{
    public IReadOnlyList<PrinterFeedUpdate> Updates { get; init; } = Array.Empty<PrinterFeedUpdate>();
    public IReadOnlyList<OrderFeedParseError> UpdateErrors { get; init; } = Array.Empty<OrderFeedParseError>();
    public string? NextUpdateCursor { get; init; }
    public bool HasMoreUpdates { get; init; }
    public bool IsSuccess { get; init; } = true;
    public bool HasDataEnvelope { get; init; }
    public string? FailureMessage { get; init; }
}
/// <summary>A single order or update element that could not be read from the feed.</summary>
public sealed record OrderFeedParseError(int Index, string? OrderNumber, string Message);
