using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

public class OrderHistoryService : IOrderHistoryService
{
    private readonly ILogger<OrderHistoryService> _logger;
    private readonly ObservableCollection<OrderHistoryItem> _orders;
    private readonly object _lockObject = new();

    // External callers see the read-only wrapper so they cannot bypass
    // the 100-item cap or the _lockObject-guarded mutators below by
    // calling .Add() / .Clear() directly on the inner collection.
    // CollectionChanged events propagate through the wrapper.
    public ReadOnlyObservableCollection<OrderHistoryItem> Orders { get; }

    public event EventHandler<OrderHistoryItem>? OrderAdded;

    public OrderHistoryService(ILogger<OrderHistoryService> logger)
    {
        _logger = logger;
        _orders = new ObservableCollection<OrderHistoryItem>();
        Orders = new ReadOnlyObservableCollection<OrderHistoryItem>(_orders);
    }

    public void AddOrder(OrderEvent orderEvent)
    {
        try
        {
            if (orderEvent.Order == null)
                return;

            // Must be on main thread for ObservableCollection UI binding to work
            if (MainThread.IsMainThread)
            {
                AddOrderInternal(orderEvent);
            }
            else
            {
                MainThread.BeginInvokeOnMainThread(() => AddOrderInternal(orderEvent));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding order to history");
        }
    }

    private void AddOrderInternal(OrderEvent orderEvent)
    {
        lock (_lockObject)
        {
            // Check if order already exists (avoid duplicates in UI)
            if (_orders.Any(o => o.Order.OrderNumber == orderEvent.Order!.OrderNumber))
            {
                _logger.LogInformation("Order #{OrderNumber} already in history, skipping", orderEvent.Order.OrderNumber);
                return;
            }

            var historyItem = new OrderHistoryItem
            {
                Order = orderEvent.Order!,
                EventType = orderEvent.EventType,
                ReceivedAt = DateTime.UtcNow,
                KitchenPrinted = false,
                CashierPrinted = false,
                Status = orderEvent.Order.Status
            };

            // Insert at the beginning (most recent first)
            _orders.Insert(0, historyItem);

            _logger.LogInformation("Order #{OrderNumber} added to history (total: {Count})",
                orderEvent.Order.OrderNumber, _orders.Count);

            // Notify subscribers
            OrderAdded?.Invoke(this, historyItem);

            // Keep only last 100 orders to prevent memory issues
            if (_orders.Count > 100)
            {
                _orders.RemoveAt(_orders.Count - 1);
            }
        }
    }

    public void UpdatePrintStatus(string orderId, bool kitchenPrinted, bool cashierPrinted)
    {
        // OrderHistoryItem raises PropertyChanged from its setters; marshal to the UI thread so the
        // bound CollectionView updates safely (mirrors AddOrder). Callers come from print completion
        // on background threads.
        if (MainThread.IsMainThread)
        {
            UpdatePrintStatusInternal(orderId, kitchenPrinted, cashierPrinted);
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(() => UpdatePrintStatusInternal(orderId, kitchenPrinted, cashierPrinted));
        }
    }

    private void UpdatePrintStatusInternal(string orderId, bool kitchenPrinted, bool cashierPrinted)
    {
        lock (_lockObject)
        {
            var order = _orders.FirstOrDefault(o => o.Order.Id == orderId);
            if (order != null)
            {
                order.KitchenPrinted = kitchenPrinted;
                order.CashierPrinted = cashierPrinted;
                order.LastPrintedAt = DateTime.UtcNow;
            }
        }
    }

    public void ClearHistory()
    {
        lock (_lockObject)
        {
            _orders.Clear();
            _logger.LogInformation("Order history cleared");
        }
    }

    public OrderHistoryItem? GetOrder(string orderId)
    {
        lock (_lockObject)
        {
            return _orders.FirstOrDefault(o => o.Order.Id == orderId);
        }
    }
}

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
