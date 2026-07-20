using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PrinterAPP.Services;

// LogType + LogEntry live in their own file (extracted from RequestLogService.cs) and are deliberately
// MAUI-free: they cross the IRequestLogService boundary, so the headless E2E harness (net10.0) can
// source-link the interface + this model without dragging in MAUI. The LogType→Color mapping that used
// to live here as `TypeColor` now lives in Converters/ValueConverters.cs (LogTypeToColorConverter),
// where UI concerns belong. See docs/E2E-STRATEGY.md.
public enum LogType
{
    SSE,
    Order,
    PrintRequest,
    PrintSuccess,
    PrintError,
    Error,
    Warning
}

public class LogEntry : INotifyPropertyChanged
{
    private bool _isRequestExpanded = false;
    private bool _isResponseExpanded = false;
    private bool _isRequestHeadersExpanded = false;
    private bool _isResponseHeadersExpanded = false;
    private bool _isRequestBodyExpanded = false;
    private bool _isResponseBodyExpanded = false;

    public DateTime Timestamp { get; set; }
    public LogType Type { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string Source { get; set; } = "General"; // Kitchen, Service, or General

    // HTTP Request Details
    public string? RequestUrl { get; set; }
    public string? RequestMethod { get; set; }
    public Dictionary<string, string>? RequestHeaders { get; set; }
    public string? RequestBody { get; set; }

    // HTTP Response Details
    public int? ResponseStatusCode { get; set; }
    public Dictionary<string, string>? ResponseHeaders { get; set; }
    public string? ResponseBody { get; set; }

    // Expansion State Properties
    public bool IsRequestExpanded
    {
        get => _isRequestExpanded;
        set
        {
            if (_isRequestExpanded != value)
            {
                _isRequestExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RequestExpandIcon));
            }
        }
    }

    public bool IsResponseExpanded
    {
        get => _isResponseExpanded;
        set
        {
            if (_isResponseExpanded != value)
            {
                _isResponseExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ResponseExpandIcon));
            }
        }
    }

    public bool IsRequestHeadersExpanded
    {
        get => _isRequestHeadersExpanded;
        set
        {
            if (_isRequestHeadersExpanded != value)
            {
                _isRequestHeadersExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RequestHeadersExpandIcon));
            }
        }
    }

    public bool IsResponseHeadersExpanded
    {
        get => _isResponseHeadersExpanded;
        set
        {
            if (_isResponseHeadersExpanded != value)
            {
                _isResponseHeadersExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ResponseHeadersExpandIcon));
            }
        }
    }

    public bool IsRequestBodyExpanded
    {
        get => _isRequestBodyExpanded;
        set
        {
            if (_isRequestBodyExpanded != value)
            {
                _isRequestBodyExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RequestBodyExpandIcon));
            }
        }
    }

    public bool IsResponseBodyExpanded
    {
        get => _isResponseBodyExpanded;
        set
        {
            if (_isResponseBodyExpanded != value)
            {
                _isResponseBodyExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ResponseBodyExpandIcon));
            }
        }
    }

    // Expand Icons
    public string RequestExpandIcon => IsRequestExpanded ? "▼" : "▶";
    public string ResponseExpandIcon => IsResponseExpanded ? "▼" : "▶";
    public string RequestHeadersExpandIcon => IsRequestHeadersExpanded ? "▼" : "▶";
    public string ResponseHeadersExpandIcon => IsResponseHeadersExpanded ? "▼" : "▶";
    public string RequestBodyExpandIcon => IsRequestBodyExpanded ? "▼" : "▶";
    public string ResponseBodyExpandIcon => IsResponseBodyExpanded ? "▼" : "▶";

    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");

    public string TypeIcon => Type switch
    {
        LogType.SSE => "🌐",
        LogType.Order => "📋",
        LogType.PrintRequest => "🖨️",
        LogType.PrintSuccess => "✅",
        LogType.PrintError => "❌",
        LogType.Error => "⚠️",
        LogType.Warning => "⚡",
        _ => "📝"
    };

    public string DisplayText => $"[{TimestampText}] {TypeIcon} {Operation}: {Message}";

    public bool HasRequestDetails => !string.IsNullOrEmpty(RequestUrl) || RequestHeaders?.Any() == true || !string.IsNullOrEmpty(RequestBody);
    public bool HasResponseDetails => ResponseStatusCode.HasValue || ResponseHeaders?.Any() == true || !string.IsNullOrEmpty(ResponseBody);

    public string RequestHeadersText => RequestHeaders != null
        ? string.Join("\n", RequestHeaders.Select(h => $"{h.Key}: {h.Value}"))
        : string.Empty;

    public string ResponseHeadersText => ResponseHeaders != null
        ? string.Join("\n", ResponseHeaders.Select(h => $"{h.Key}: {h.Value}"))
        : string.Empty;

    // Formatted JSON Properties
    public string RequestHeadersJson => FormatAsJson(RequestHeaders);
    public string ResponseHeadersJson => FormatAsJson(ResponseHeaders);
    public string RequestBodyJson => FormatJson(RequestBody);
    public string ResponseBodyJson => FormatJson(ResponseBody);

    private static string FormatAsJson(Dictionary<string, string>? dictionary)
    {
        if (dictionary == null || !dictionary.Any())
            return string.Empty;

        try
        {
            var json = JsonSerializer.Serialize(dictionary, new JsonSerializerOptions { WriteIndented = true });
            return json;
        }
        catch
        {
            return string.Join("\n", dictionary.Select(h => $"{h.Key}: {h.Value}"));
        }
    }

    private static string FormatJson(string? jsonString)
    {
        if (string.IsNullOrWhiteSpace(jsonString))
            return string.Empty;

        try
        {
            // Try to parse and format as JSON
            using var document = JsonDocument.Parse(jsonString);
            return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            // If not valid JSON, return as-is
            return jsonString;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
