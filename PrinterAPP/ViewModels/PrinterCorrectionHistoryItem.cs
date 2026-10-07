using PrinterAPP.Models;
using PrinterAPP.Services;

namespace PrinterAPP.ViewModels;

public sealed record PrinterCorrectionHistoryItem(PrintUpdateJobRecord Record)
{
    public PrinterFeedUpdate Update => Record.Update;
    public PrintUpdateJobKey Key => Record.Key;
    public string State => Update.IsWithdrawn && Record.State != PrintUpdateJobState.Withdrawn
        ? $"{Record.State} · Withdrawn"
        : Record.State.ToString();
    public bool CanCopy => !Update.IsWithdrawn
        && PrinterCorrectionCopyService.IsCopySafeState(Record.State);
    public string OrderNumber => string.IsNullOrWhiteSpace(Update.OrderNumber)
        ? Update.JobId.ToString("N")[..8]
        : Update.OrderNumber;
    public string Target => Update.Target.ToString();
    public DateTime FirstSeenAt => Record.FirstSeenAt.ToLocalTime();
    public string JobRevision => $"Job {Update.JobId.ToString("N")[..8]} · revision {Update.Revision}";
    public string ChangeSummary => Update.IsWithdrawn
        ? "Withdrawn by the server. Cached correction content was removed."
        : Limit(Update.Changes.Count > 0
            ? string.Join("; ", Update.Changes.Select(DescribeChange))
            : Update.Text);
    public string FailureReason => Record.FailureReason ?? string.Empty;
    public bool HasFailureReason => !string.IsNullOrWhiteSpace(Record.FailureReason);

    private static string DescribeChange(PrinterFeedChange change)
    {
        var previous = change.Previous?.ProductName;
        var current = change.Current?.ProductName;
        return change.Kind switch
        {
            KitchenChangeKind.Add => $"Add: {current ?? "item"}",
            KitchenChangeKind.Void => $"Cancel: {previous ?? "item"}",
            KitchenChangeKind.Replace => $"Change: {previous ?? "item"} → {current ?? "item"}",
            KitchenChangeKind.InstructionChange => $"Instructions: {current ?? previous ?? "item"}",
            _ => "Review update",
        };
    }

    private static string Limit(string value) => value.Length <= 180 ? value : value[..177] + "…";
}
