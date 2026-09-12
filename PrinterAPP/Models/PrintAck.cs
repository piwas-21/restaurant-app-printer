namespace PrinterAPP.Models;

/// <summary>
/// One order or additive update outcome, sent to <c>POST /api/devices/print-acks</c>. The device id
/// travels in the <c>X-Device-Id</c> header. Job fields stay nullable for legacy order acks, but an
/// update ack always carries all three identity fields.
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
    public Guid? JobId { get; set; }
    public int? Revision { get; set; }
    public DevicePrintJobType? JobType { get; set; }
}
