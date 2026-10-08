using System.Text;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

public interface IMarketplaceReceiptComposer
{
    string? Currency(Order order);
    bool CanPrint(Order order, PrinterType type);
    string ProviderName(ExternalOrder source);
    void AppendIdentity(StringBuilder builder, Order order, string language, bool showPayment);
    void AppendTax(StringBuilder builder, Order order, PrintLabels labels, string language);
}
