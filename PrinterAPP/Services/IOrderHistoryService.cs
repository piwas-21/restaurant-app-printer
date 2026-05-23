using System.Collections.ObjectModel;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

public interface IOrderHistoryService
{
    ReadOnlyObservableCollection<OrderHistoryItem> Orders { get; }
    event EventHandler<OrderHistoryItem>? OrderAdded;

    void AddOrder(OrderEvent orderEvent);
    void UpdatePrintStatus(string orderId, bool kitchenPrinted, bool cashierPrinted);
    void ClearHistory();
    OrderHistoryItem? GetOrder(string orderId);
}
