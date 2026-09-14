using PrinterAPP.Models;
using PrinterAPP.Services;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// Print-language resolution and the label catalog. Every arm of
/// <see cref="PrintLanguagePolicy.Resolve"/> is pinned (auto with/without an order preference,
/// unknown codes, unsupported scripts), and every supported language is required to be fully
/// populated — a half-translated catalog would print English labels mid-ticket.
/// </summary>
public class PrintLanguagePolicyTests
{
    [Theory]
    [InlineData("auto", "fr", "fr")] // auto follows the order's language
    [InlineData("auto", "tr", "tr")]
    [InlineData("AUTO", "tr", "tr")] // setting match is case-insensitive
    [InlineData("auto", null, "en")] // no order preference -> English fallback
    [InlineData("auto", "", "en")]
    [InlineData("auto", "zh", "en")] // script the receipt codepage cannot render -> fallback
    [InlineData("auto", "fr-CH", "en")] // a raw tag, not a bare code -> fallback
    [InlineData("de", "fr", "de")]   // a fixed venue choice beats the order's language
    [InlineData("tr", null, "tr")]
    [InlineData("it", null, "it")]
    [InlineData(null, "fr", "en")]   // blank setting -> English
    [InlineData("ru", null, "en")]   // unsupported code -> English, never garbage
    [InlineData("klingon", "fr", "en")]
    public void Resolve_covers_every_arm(string? configured, string? preferred, string expected)
    {
        Assert.Equal(expected, PrintLanguagePolicy.Resolve(configured, preferred));
    }

    [Fact]
    public void Every_supported_language_resolves_to_itself_and_has_a_display_name()
    {
        foreach (var code in PrintLanguagePolicy.Supported)
        {
            Assert.Equal(code, PrintLanguagePolicy.Resolve(code, null));
            Assert.True(PrintLanguagePolicy.DisplayNames.ContainsKey(code));
            Assert.False(string.IsNullOrWhiteSpace(PrintLanguagePolicy.DisplayNames[code]));
        }
    }

    /// <summary>No half-populated catalog: every label in every language must be non-blank.</summary>
    [Fact]
    public void Every_language_has_every_label_populated()
    {
        foreach (var code in PrintLanguagePolicy.Supported)
        {
            var labels = PrintLabelCatalog.For(code);
            foreach (var value in new[]
                     {
                         labels.OnlineOrder, labels.Type, labels.Table, labels.Customer, labels.Tel,
                         labels.Notes, labels.Note, labels.NoPrefix, labels.ExtraPrefix,
                         labels.SelectedPrefix, labels.Subtotal, labels.Tax, labels.Discount,
                         labels.CustomerDiscount, labels.Promo, labels.DeliveryFee, labels.Tip,
                         labels.Total, labels.Payment, labels.CardAtRestaurant, labels.Paid, labels.Due, labels.DeliveryTo,
                         labels.Instructions, labels.ThankYou, labels.DineIn, labels.TakeAway,
                         labels.Delivery, labels.NoItems,
                     })
            {
                Assert.False(string.IsNullOrWhiteSpace(value), $"{code} has a blank label: '{value}'");
            }
        }
    }

    [Fact]
    public void Unknown_language_falls_back_to_english()
    {
        Assert.Same(PrintLabelCatalog.English, PrintLabelCatalog.For("zh"));
        Assert.Same(PrintLabelCatalog.English, PrintLabelCatalog.For(null));
        Assert.Same(PrintLabelCatalog.English, PrintLabelCatalog.For("auto")); // auto is a policy value, not a catalog language
    }

    [Fact]
    public void Order_type_names_map_the_backend_enum_values()
    {
        var en = PrintLabelCatalog.For("en");
        Assert.Equal("Dine-in", en.OrderType("DineIn"));
        Assert.Equal("Takeaway", en.OrderType("TakeAway"));
        Assert.Equal("Delivery", en.OrderType("Delivery"));
        // An unknown value prints verbatim rather than lying about the order.
        Assert.Equal("Mystery", en.OrderType("Mystery"));

        var de = PrintLabelCatalog.For("de");
        Assert.Equal("Im Lokal", de.OrderType("DineIn"));
        Assert.Equal("Mitnehmen", de.OrderType("TakeAway"));
        Assert.Equal("Lieferung", de.OrderType("Delivery"));
    }
}
