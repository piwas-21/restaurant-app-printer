using System.ComponentModel;
using System.Runtime.CompilerServices;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>
/// One order's row in the in-memory history — the order, how it arrived, and its print outcome.
/// Moved here verbatim from OrderHistoryService so the MAUI-free test project can link
/// <see cref="IOrderHistoryService"/> (which this type appears on) without dragging the
/// MainThread-bound service along.
/// </summary>
public class OrderHistoryItem : INotifyPropertyChanged
{
    public Order Order { get; set; } = null!;
    public string EventType { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }

    private bool _kitchenPrinted;
    public bool KitchenPrinted
    {
        get => _kitchenPrinted;
        set
        {
            if (_kitchenPrinted == value) return;
            _kitchenPrinted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PrintOutcome)); // derived
        }
    }

    private bool _cashierPrinted;
    public bool CashierPrinted
    {
        get => _cashierPrinted;
        set
        {
            if (_cashierPrinted == value) return;
            _cashierPrinted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PrintOutcome)); // derived
        }
    }

    private DateTime? _lastPrintedAt;
    public DateTime? LastPrintedAt
    {
        get => _lastPrintedAt;
        set
        {
            if (_lastPrintedAt == value) return;
            _lastPrintedAt = value;
            OnPropertyChanged();
        }
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
        }
    }

    public string DisplayText => $"Order #{Order.OrderNumber} - {Order.TypeDisplay} - {Order.Items.Count} items - ${Order.Total:F2}";
    public string ReceivedAtText => ReceivedAt.ToLocalTime().ToString("HH:mm:ss");
    // Semantic print outcome (data, not presentation) — PrintOutcomeToColorConverter maps it to a craft
    // colour in the UI layer. Was a "Green"/"Orange"/"Red" colour string on the model (a presentation leak).
    public string PrintOutcome => KitchenPrinted && CashierPrinted ? "Printed" : CashierPrinted || KitchenPrinted ? "Partial" : "None";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
