# ADR-008 — Marketplace source and permission-aware receipts

**Status:** Proposed
**Date:** 2026-10-01
**Implements:** Sofra delivery-channels integration
**References:**
- Backend `Features/Orders/Dtos/ExternalOrderDto.cs` (backend PR #629)
- `PrinterAPP/Services/MarketplaceReceiptComposer.cs`
- `PrinterAPP.Tests/OrderPrintToSinkTests.cs`

## Context

Marketplace orders carry a frozen currency, provider payment custody and nullable reported tax.
The ordinary order tax field cannot distinguish unreported tax from a reported zero. Imported orders
can also be held while the provider decision is pending; sending a kitchen ticket then would start
preparation before acceptance. Manual reprint must obey the same boundary as automatic printing.

## Decision

Mirror the backend's optional source DTO additively and preserve it when filtering items by kitchen.
Compose provider/display identity and a test marker on both ticket surfaces. Cashier receipts use the
validated frozen source currency, show provider payment custody and distinguish unknown tax from zero.
An invalid source currency produces unlabelled amounts and never falls back to a tenant or tender label.
Kitchen tickets retain instructions and identity while omitting payment and money information.

External cashier output requires explicit `PrintReceipt` permission. External kitchen output requires
both `IsKitchenReleased` and explicit `PrintKitchen` permission. A blocked kitchen destination reports
`Unknown`, preventing a false successful print acknowledgement. Orders without source metadata preserve
the existing print behavior. The stateless composer is registered through its interface in `MauiProgram`; source-linked tests inject the same implementation.

## Consequences

Provider business decisions remain in the backend/gateway. The printer cannot release an order itself,
and an older backend that omits external permissions produces no external ticket. Both MAUI heads compile
in PR CI; TCP sink tests verify the actual composition/transport path. Device output and release packaging
remain separate acceptance checks. The labels follow the seven existing PC857 print languages and fallback.

## Security boundary

- Source display references are text; control characters are removed before ESC/POS composition.
- External currency labels accept only three uppercase ASCII letters and never use tender fallback.
- Missing external print permissions deny output; manual printing enforces the same rules.
- A held order cannot generate a kitchen ticket or a successful kitchen acknowledgement.
- The additive model contains display/payment evidence only, with no provider credentials or internal IDs.

## Alternatives considered

Using the ordinary payment label/tax field would misrepresent provider custody or unknown tax. Allowing
manual reprint to bypass release/permissions would start a held order. Both alternatives were rejected.
