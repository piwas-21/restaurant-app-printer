using PrinterAPP.Models;
using Xunit;

namespace PrinterAPP.Tests;

/// <summary>
/// Guards the printer's typed payment-method contract against the backend enum's numeric values.
/// Numeric values are part of the legacy feed wire format even though <see cref="Payment.PaymentMethod"/>
/// remains a string to preserve all backend DTO payloads and aliases.
/// </summary>
public class PaymentMethodCodeContractTests
{
    [Fact]
    public void Payment_method_codes_match_the_backend_enum_in_full()
    {
        var expected = new Dictionary<string, int>
        {
            ["Cash"] = 1,
            ["CreditCard"] = 2,
            ["DebitCard"] = 3,
            ["OnlinePayment"] = 4,
            ["MobilePayment"] = 5,
            ["BankTransfer"] = 6,
        };
        var actual = Enum.GetValues<PaymentMethodCode>()
            .ToDictionary(method => method.ToString(), method => (int)method);

        Assert.Equal(expected, actual);
    }
}
