using PrinterAPP.Models;
using Xunit;

namespace PrinterAPP.Tests;

public sealed class TableDisplayTests
{
    [Fact]
    public void ResolveLabel_prefers_nonblank_label_over_numeric_compatibility_value()
    {
        Assert.Equal("T-QA", TableDisplay.ResolveLabel(" T-QA ", 7));
        Assert.Equal("DineIn - Table T-QA", TableDisplay.FormatOrderType("DineIn", "T-QA", 7, "Table"));
    }

    [Theory]
    [InlineData(7, "7")]
    [InlineData(1, "1")]
    public void ResolveLabel_uses_positive_numeric_fallback(int tableNumber, string expected)
    {
        Assert.Equal(expected, TableDisplay.ResolveLabel(null, tableNumber));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void ResolveLabel_omits_nonpositive_numeric_identity(int number)
    {
        Assert.Null(TableDisplay.ResolveLabel(null, number));
    }

    [Fact]
    public void ResolveLabel_omits_blank_identity()
    {
        Assert.Null(TableDisplay.ResolveLabel(null, null));
        Assert.Null(TableDisplay.ResolveLabel(string.Empty, null));
        Assert.Null(TableDisplay.ResolveLabel("  ", null));
    }

    [Fact]
    public void ResolveLabel_replaces_control_characters_without_emitting_them()
    {
        var label = Assert.IsType<string>(TableDisplay.ResolveLabel(" T\u001b@\r\nQA ", null));

        Assert.Equal("T @ QA", label);
        Assert.DoesNotContain('\u001b', label);
        Assert.DoesNotContain('\r', label);
        Assert.DoesNotContain('\n', label);
    }

    [Fact]
    public void Order_type_display_keeps_legacy_numeric_orders_readable()
    {
        var order = new Order { Type = "DineIn", TableNumber = 7 };

        Assert.Equal("DineIn - Table 7", order.TypeDisplay);
    }
}
