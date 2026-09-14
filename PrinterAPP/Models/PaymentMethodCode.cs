namespace PrinterAPP.Models;

/// <summary>
/// Numeric payment-method codes accepted by the backend's legacy wire representation.
/// </summary>
// Source of truth: backend/RestaurantSystem.Domain/Common/Enums/PaymentMethod.cs.
// Keep names and numeric values in lockstep with that backend enum.
public enum PaymentMethodCode
{
    Cash = 1,
    CreditCard = 2,
    DebitCard = 3,
    OnlinePayment = 4,
    MobilePayment = 5,
    BankTransfer = 6,
}
