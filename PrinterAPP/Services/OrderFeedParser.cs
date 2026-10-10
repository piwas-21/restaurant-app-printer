using System.Text.Json;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>Resilient parser for orders and additive update jobs in one printer-feed response.</summary>
public static partial class OrderFeedParser
{
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
        ReadProjectionVersion(data, parsed);
        ParseOrdersIfPresent(data, parsed);
        ReadNextOrderCursor(data, parsed);
        ReadHasMoreOrders(data, parsed);
        ValidateOrderPage(parsed);
        ReadNextUpdateCursor(data, parsed);
        ReadHasMoreUpdates(data, parsed);
        ParseUpdatesIfPresent(data, parsed);
        ValidateUpdatePage(parsed);
        return parsed;
    }
    private static void ReadProjectionVersion(JsonElement data, ParsedData parsed)
    {
        if (!TryGetPropertyIgnoreCase(data, "projectionVersion", out var version)
            || version.ValueKind == JsonValueKind.Null)
            return;
        if (version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var value) && value > 0)
        {
            parsed.ProjectionVersion = value;
            return;
        }
        parsed.Fail("The projection version was not a positive integer or null.");
    }
    private static void ReadNextOrderCursor(JsonElement data, ParsedData parsed)
    {
        if (!TryGetPropertyIgnoreCase(data, "nextOrderCursor", out var cursor))
            return;
        if (cursor.ValueKind == JsonValueKind.String)
        {
            parsed.NextOrderCursor = cursor.GetString();
            if (string.IsNullOrWhiteSpace(parsed.NextOrderCursor))
                parsed.NextOrderCursor = null;
            return;
        }
        if (cursor.ValueKind != JsonValueKind.Null)
            parsed.Fail("The order cursor was not a string or null.");
    }
    private static void ReadHasMoreOrders(JsonElement data, ParsedData parsed)
    {
        if (!TryGetPropertyIgnoreCase(data, "hasMoreOrders", out var more))
            return;
        if (more.ValueKind == JsonValueKind.True)
        {
            parsed.HasMoreOrders = true;
            return;
        }
        if (more.ValueKind != JsonValueKind.False)
            parsed.Fail("The order page flag was not a boolean.");
    }
    private static void ValidateOrderPage(ParsedData parsed)
    {
        if (parsed.HasMoreOrders && string.IsNullOrWhiteSpace(parsed.NextOrderCursor))
            parsed.Fail("An order page requires a non-empty next cursor.");
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
            ParseUpdates(updateArray, parsed.Updates, parsed.UpdateErrors, parsed.ProjectionVersion >= 2);
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
        Updates = parsed.Updates,
        UpdateErrors = parsed.UpdateErrors,
        NextOrderCursor = parsed.NextOrderCursor,
        ProjectionVersion = parsed.ProjectionVersion,
        HasMoreOrders = parsed.HasMoreOrders,
        NextUpdateCursor = parsed.NextUpdateCursor,
        HasMoreUpdates = parsed.HasMoreUpdates,
        IsSuccess = parsed.IsSuccess,
        HasDataEnvelope = true,
        FailureMessage = parsed.FailureMessage,
    };
    private static OrderFeedParseResult InvalidBody(Exception ex) => new(
        Array.Empty<Order>(), new[] { new OrderFeedParseError(-1, null, $"Response body was not valid JSON: {ex.Message}") })
    {
        IsSuccess = false,
        HasDataEnvelope = false,
    };
    private static OrderFeedParseResult InvalidEnvelope(string? failureMessage) => new(
        Array.Empty<Order>(), Array.Empty<OrderFeedParseError>())
    {
        IsSuccess = false,
        HasDataEnvelope = false,
        FailureMessage = failureMessage,
    };
    private static void ParseUpdates(JsonElement updateArray, ICollection<PrinterFeedUpdate> updates,
        ICollection<OrderFeedParseError> errors, bool isProjectionV2)
    {
        var index = 0;
        foreach (var element in updateArray.EnumerateArray())
        {
            try
            {
                var update = element.Deserialize<PrinterFeedUpdate>(PrinterJsonSerialization.Options);
                if (update is null)
                    errors.Add(new OrderFeedParseError(index, TryReadUpdateOrderNumber(element), "Update element deserialised to null."));
                else
                {
                    if (isProjectionV2)
                        OrderPresentationMetadataNormalizer.Normalize(update);
                    var validationError = PrinterFeedUpdateValidation.Validate(element, update);
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
    private static string? TryReadOrderNumber(JsonElement element) => TryReadScalar(element, "orderNumber");
    private static string? TryReadUpdateOrderNumber(JsonElement element) => TryReadScalar(element, "orderNumber");
    private static string? TryReadScalar(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !TryGetPropertyIgnoreCase(element, propertyName, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
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
        public string? NextOrderCursor { get; set; }
        public int? ProjectionVersion { get; set; }
        public bool HasMoreOrders { get; set; }
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
    public string? NextOrderCursor { get; init; }
    /// <summary>Null for V1/legacy feeds that do not identify a presentation projection.</summary>
    public int? ProjectionVersion { get; init; }
    public bool HasMoreOrders { get; init; }
    public string? NextUpdateCursor { get; init; }
    public bool HasMoreUpdates { get; init; }
    public bool IsSuccess { get; init; } = true;
    public bool HasDataEnvelope { get; init; }
    public string? FailureMessage { get; init; }
}
/// <summary>A single order or update element that could not be read from the feed.</summary>
public sealed record OrderFeedParseError(int Index, string? OrderNumber, string Message);
