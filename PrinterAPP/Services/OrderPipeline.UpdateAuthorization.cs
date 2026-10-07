using Microsoft.Extensions.Logging;
using PrinterAPP.Models;

namespace PrinterAPP.Services;

public partial class OrderPipeline
{
    private async Task<AuthorizedUpdateResult> PrintAuthorizedUpdateAsync(PrinterFeedUpdate update)
    {
        var preflightResult = await CheckInitialUpdateAuthorizationAsync(update);
        if (preflightResult is not null)
            return preflightResult;

        return await PrintPreauthorizedUpdateAsync(update);
    }

    private async Task<AuthorizedUpdateResult?> CheckInitialUpdateAuthorizationAsync(PrinterFeedUpdate update)
    {
        if (IsLocallyWithdrawn(update))
            return WithdrawnUpdate();

        if (_updateAuthorizationService is null)
            return AuthorizationUnavailable();

        PrinterUpdateAuthorizationResult authorization;
        try
        {
            authorization = await _updateAuthorizationService.CheckAsync(update, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Authorization check failed for update {JobId}; no printer bytes were sent", update.JobId);
            return IsLocallyWithdrawn(update) ? WithdrawnUpdate() : AuthorizationUnavailable();
        }

        if (authorization.Status == PrinterUpdateAuthorizationStatus.Withdrawn)
            return PersistBackendWithdrawal(update);

        // The feed may deliver a withdrawal while the authority request is in flight. Recheck the
        // durable local fence immediately before composing or opening the printer transport.
        if (IsLocallyWithdrawn(update))
            return WithdrawnUpdate();

        return authorization.Status == PrinterUpdateAuthorizationStatus.Authorized
            ? null
            : AuthorizationUnavailable();
    }

    private async Task<AuthorizedUpdateResult> PrintPreauthorizedUpdateAsync(PrinterFeedUpdate update)
    {
        var finalAuthorization = PrinterUpdateAuthorizationResult.Authorized;
        var withdrawalPersistenceFailed = false;
        try
        {
            var outcome = await _orderPrintService.PrintUpdateAsync(
                update,
                cancellationToken => AuthorizeImmediatelyBeforeSendAsync(
                    update,
                    cancellationToken,
                    result => finalAuthorization = result,
                    () => withdrawalPersistenceFailed = true),
                CancellationToken.None);
            if (withdrawalPersistenceFailed)
            {
                return new(KitchenPrintOutcome.Unknown, PrintUpdateJobState.Unknown,
                    "Backend withdrawal was received, but local redaction could not be persisted.");
            }

            if (finalAuthorization.Status == PrinterUpdateAuthorizationStatus.Withdrawn)
                return WithdrawnUpdate();
            if (finalAuthorization.Status != PrinterUpdateAuthorizationStatus.Authorized)
                return AuthorizationUnavailable();

            var state = outcome.Status switch
            {
                KitchenPrintStatus.Sent => PrintUpdateJobState.Sent,
                KitchenPrintStatus.Skipped => PrintUpdateJobState.Skipped,
                KitchenPrintStatus.NotConfigured => PrintUpdateJobState.NotConfigured,
                KitchenPrintStatus.Unknown => PrintUpdateJobState.Unknown,
                _ => PrintUpdateJobState.Failed,
            };
            var reason = state is PrintUpdateJobState.Failed
                or PrintUpdateJobState.NotConfigured or PrintUpdateJobState.Unknown
                ? outcome.Status.ToString() : null;
            return new(outcome, state, reason);
        }
        catch (Exception ex)
        {
            // A thrown print call may have failed after handing bytes to the transport. Treat it as
            // ambiguous and wait for operator review instead of automatically duplicating a ticket.
            _logger.LogError(ex,
                "Update job {JobId} failed during printing with an unknown delivery state", update.JobId);
            return new(KitchenPrintOutcome.Unknown, PrintUpdateJobState.Unknown, "Unknown");
        }
    }

    private AuthorizedUpdateResult? PersistBackendWithdrawal(PrinterFeedUpdate update)
    {
        if (_updateJobStore?.MarkWithdrawn(update.Key) is { } persisted && !persisted)
        {
            _logger.LogError(
                "Could not persist withdrawal for update {JobId}; output is blocked pending local recovery",
                update.JobId);
            return new(KitchenPrintOutcome.Unknown, PrintUpdateJobState.Unknown,
                "Backend withdrew this update, but local redaction could not be persisted.");
        }

        return WithdrawnUpdate();
    }

    private bool IsLocallyWithdrawn(PrinterFeedUpdate update) =>
        update.IsWithdrawn || _updateJobStore?.IsWithdrawalRequested(update.Key) == true;

    private async Task<PrinterUpdateAuthorizationResult> AuthorizeImmediatelyBeforeSendAsync(
        PrinterFeedUpdate update,
        CancellationToken cancellationToken,
        Action<PrinterUpdateAuthorizationResult> setFinalAuthorization,
        Action markWithdrawalPersistenceFailed)
    {
        if (_updateJobStore?.IsWithdrawalRequested(update.Key) == true)
        {
            setFinalAuthorization(PrinterUpdateAuthorizationResult.Withdrawn);
            return PrinterUpdateAuthorizationResult.Withdrawn;
        }

        if (_updateAuthorizationService is null)
            return PrinterUpdateAuthorizationResult.Unavailable;

        PrinterUpdateAuthorizationResult final;
        try
        {
            final = await _updateAuthorizationService.CheckAsync(update, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Final authorization check failed for update {JobId}; no printer bytes were sent",
                update.JobId);
            final = PrinterUpdateAuthorizationResult.Unavailable;
        }

        if (final.Status == PrinterUpdateAuthorizationStatus.Withdrawn)
        {
            if (_updateJobStore?.MarkWithdrawn(update.Key) is { } persisted && !persisted)
            {
                markWithdrawalPersistenceFailed();
                final = PrinterUpdateAuthorizationResult.Unavailable;
            }
        }
        else if (final.Status == PrinterUpdateAuthorizationStatus.Authorized
            && _updateJobStore?.IsWithdrawalRequested(update.Key) == true)
        {
            final = PrinterUpdateAuthorizationResult.Withdrawn;
        }

        setFinalAuthorization(final);
        return final;
    }

    private static AuthorizedUpdateResult WithdrawnUpdate() => new(
        KitchenPrintOutcome.Skipped,
        PrintUpdateJobState.Withdrawn,
        "Withdrawn");

    private static AuthorizedUpdateResult AuthorizationUnavailable() => new(
        KitchenPrintOutcome.Failed,
        PrintUpdateJobState.Failed,
        "Print authorization unavailable; no printer bytes were sent.");

    private sealed record AuthorizedUpdateResult(
        KitchenPrintOutcome Outcome,
        PrintUpdateJobState State,
        string? FailureReason);
}
