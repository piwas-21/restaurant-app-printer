namespace PrinterAPP.Models;

/// <summary>
/// One order/target print outcome, sent in the body of <c>POST /api/devices/print-acks</c>. Mirrors
/// the backend <c>PrintAckDto</c> (the device id travels in the <c>X-Device-Id</c> header, not here).
/// Non-PII: order id + target + outcome + timestamps only — never customer data or receipt content.
/// </summary>
public class PrintAck
{
    public Guid OrderId { get; set; }
    public DevicePrintTarget Target { get; set; }
    public DevicePrintStatus Status { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime? PrintedAt { get; set; }
    public string? FailureReason { get; set; }
    public int Copies { get; set; }
}
