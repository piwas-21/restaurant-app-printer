using System.Globalization;
using System.Text;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

/// <summary>Shared receipt lines and pure composition helpers, independent of MAUI and printer I/O.</summary>
public static partial class ReceiptComposer
{
    public static string Money(decimal amount) => amount.ToString("F2", CultureInfo.InvariantCulture);

    public static string Money(decimal amount, string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? Money(amount) : $"{currency} {Money(amount)}";

    public static void AppendTypeAndTableLine(StringBuilder sb, Order order, PrintLabels labels)
    {
        var orderType = TableDisplay.FormatOrderType(
            labels.OrderType(order.Type), order.TableLabel, order.TableNumber, labels.Table);
        sb.AppendLine($"{labels.Type}: {orderType}");
    }

    public static void AppendOrderNotesLine(StringBuilder sb, Order order, PrintLabels labels)
    {
        if (!string.IsNullOrWhiteSpace(order.Notes))
            sb.AppendLine($"{labels.Notes}: {order.Notes}");
    }

    public static void AppendDailyOrderNumber(StringBuilder sb, Order order)
    {
        var number = ResolveDailyOrderNumber(order);
        if (number is null)
            return;

        sb.Append(EscPosCommands.AlignCenter);
        sb.Append(EscPosCommands.SizeDouble);
        sb.Append(EscPosCommands.ExtraDarkOn);
        sb.AppendLine($"#{number.Value.ToString(CultureInfo.InvariantCulture)}");
        sb.Append(EscPosCommands.ExtraDarkOff);
        sb.Append(EscPosCommands.SizeNormal);
        sb.Append(EscPosCommands.AlignLeft);
        sb.AppendLine();
    }

    public static void AppendCashierContactLines(
        StringBuilder sb, Order order, PrinterConfiguration config, PrintLabels labels)
    {
        if (config.PrintCustomerPhoneOnCashierReceipt)
        {
            var phone = order.Type == "Delivery" ? order.DeliveryAddress?.Phone : null;
            if (string.IsNullOrWhiteSpace(phone))
                phone = order.CustomerPhone;
            if (!string.IsNullOrWhiteSpace(phone))
                sb.AppendLine($"{labels.Tel}: {UpdateReceiptComposer.SanitizeField(phone)}");
        }

        if (config.PrintCustomerEmailOnCashierReceipt && !string.IsNullOrWhiteSpace(order.CustomerEmail))
            sb.AppendLine($"{labels.Email}: {UpdateReceiptComposer.SanitizeField(order.CustomerEmail)}");
    }

    public static void AppendCashierPreparationLines(
        StringBuilder sb, Order order, PrinterConfiguration config, PrintLabels labels)
    {
        if (config.PrintPreparationMinutesOnCashierReceipt
            && order.EffectivePreparationMinutes is int minutes
            && minutes >= 0)
        {
            sb.AppendLine($"{labels.Preparation}: {minutes.ToString(CultureInfo.InvariantCulture)} min");
        }

        if (config.PrintExpectedReadyAtOnCashierReceipt
            && FormatExpectedReadyAt(order) is { } readyAt)
        {
            sb.AppendLine($"{labels.ReadyAt}: {readyAt}");
        }
    }

    public static void AppendProductGroupSeparator(StringBuilder sb, int paperWidthMillimeters) =>
        sb.AppendLine(new string('-', paperWidthMillimeters == 80 ? 48 : 32));

    public static string? FormatExpectedReadyAt(Order order)
    {
        if (order.ExpectedReadyAt is not { } expectedReadyAt
            || string.IsNullOrWhiteSpace(order.BusinessTimeZoneId))
        {
            return null;
        }

        try
        {
            var utc = DateTime.SpecifyKind(expectedReadyAt, DateTimeKind.Utc);
            var tenantTimeZone = TimeZoneInfo.FindSystemTimeZoneById(order.BusinessTimeZoneId);
            return TimeZoneInfo.ConvertTimeFromUtc(utc, tenantTimeZone)
                .ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    private static long? ResolveDailyOrderNumber(Order order)
    {
        if (order.DailyOrderNumber > 0)
            return order.DailyOrderNumber;

        var fullOrderNumber = order.OrderNumber;
        if (fullOrderNumber.Length <= 8
            || !DateOnly.TryParseExact(fullOrderNumber[..8], "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _)
            || !long.TryParse(fullOrderNumber[8..], NumberStyles.None, CultureInfo.InvariantCulture, out var fallback)
            || fallback <= 0)
        {
            return null;
        }

        return fallback;
    }
}
