using System.Text;
using System.Text.Json;
using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

public sealed class MarketplaceReceiptComposerTests
{
    private static Order SourceOrder() => new()
    {
        Currency = "CHF", Tax = 0,
        ExternalOrder = new() { Provider = "uber-eats", ExternalDisplayId = "9116D", Currency = "EUR",
            MerchantTotal = 5, ReportedTax = null, IsSandbox = true, ExternalState = "CREATED" },
    };

    [Fact]
    public void CamelCaseBackendSource_DeserializesNullTaxAndFrozenCurrency()
    {
        var order = JsonSerializer.Deserialize<Order>("""
            {"currency":"CHF","externalOrder":{"provider":"uber-eats","externalDisplayId":"9116D",
            "externalState":"CREATED","lastEventAt":"2026-10-01T17:25:07Z","currency":"EUR",
            "merchantTotal":5,"reportedTax":null,"fulfillmentType":"DELIVERY_BY_UBER","isSandbox":true}}
            """, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("EUR", MarketplaceReceiptComposer.Currency(order));
        Assert.Equal(5, order.ExternalOrder!.MerchantTotal);
        Assert.Null(order.ExternalOrder.ReportedTax);
        Assert.Equal("9116D", order.WithItems([]).ExternalOrder!.ExternalDisplayId);
    }

    [Fact]
    public void MissingTaxRemainsUnknown_ExplicitZeroIsMoney()
    {
        var order = SourceOrder(); var builder = new StringBuilder();
        MarketplaceReceiptComposer.AppendTax(builder, order, PrintLabelCatalog.English, "en");
        Assert.Equal("Tax: Not reported by provider" + Environment.NewLine, builder.ToString());
        order.ExternalOrder!.ReportedTax = 0; builder.Clear();
        MarketplaceReceiptComposer.AppendTax(builder, order, PrintLabelCatalog.English, "en");
        Assert.Equal("Tax: EUR 0.00" + Environment.NewLine, builder.ToString());
    }

    [Fact]
    public void HeldOrUnpermittedSourceCannotPrint_OrdinaryBehaviorRemains()
    {
        var order = SourceOrder();
        Assert.False(MarketplaceReceiptComposer.CanPrint(order, PrinterType.Cashier));
        Assert.False(MarketplaceReceiptComposer.CanPrint(order, PrinterType.Kitchen));
        order.PermittedActions = [new() { Action = "PrintKitchen", Allowed = true }];
        Assert.False(MarketplaceReceiptComposer.CanPrint(order, PrinterType.Kitchen));
        order.IsKitchenReleased = true;
        Assert.True(MarketplaceReceiptComposer.CanPrint(order, PrinterType.Kitchen));
        Assert.False(MarketplaceReceiptComposer.CanPrint(order, PrinterType.Cashier));
        order.ExternalOrder = null;
        Assert.True(MarketplaceReceiptComposer.CanPrint(order, PrinterType.Cashier));
    }

    [Theory]
    [InlineData("en")][InlineData("de")][InlineData("fr")][InlineData("it")]
    [InlineData("es")][InlineData("nl")][InlineData("tr")]
    public void SourceLabelsAreComplete_InEverySupportedPrintLanguage(string language)
    {
        var labels = MarketplacePrintLabels.For(language);
        Assert.False(string.IsNullOrWhiteSpace(labels.TestOrder));
        Assert.False(string.IsNullOrWhiteSpace(labels.TaxNotReported));
        Assert.Contains("{0}", labels.PaymentHandledBy);
    }

    [Fact]
    public void SourceTextCannotInjectPrinterCommandsOrCurrencyLabels()
    {
        var order = SourceOrder(); order.ExternalOrder!.ExternalDisplayId = "9116D\u001b@\nFORGED";
        order.ExternalOrder.Currency = "\u001b@";
        var builder = new StringBuilder(); MarketplaceReceiptComposer.AppendIdentity(builder, order, "en", false);
        Assert.DoesNotContain('\u001b', builder.ToString());
        Assert.DoesNotContain(Environment.NewLine + "FORGED", builder.ToString(), StringComparison.Ordinal);
        Assert.Null(MarketplaceReceiptComposer.Currency(order));
    }
}
