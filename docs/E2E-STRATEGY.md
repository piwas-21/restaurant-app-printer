# E2E Testing Strategy — RUMI Printer-App

> The printer-app's end-to-end suite. Complements the unit layer (`PrinterAPP.Tests`) and the
> manual on-device smoke ([workspace `docs/testing/fleet-observability-e2e-plan.md`](../../docs/testing/fleet-observability-e2e-plan.md)).
> Lives in `PrinterAPP.E2E/`.

## Scope: what E2E is for (and isn't)

E2E proves the printer-app's **real service code** behaves correctly against a **real backend** — the
polling feed, the fleet-telemetry client (heartbeat / print-acks / device-events), and the missed-order
reconciliation the backend derives from them. It exists to catch cross-layer bugs that unit tests can't:
JSON contract drift with the backend, `X-Api-Key` / `X-Device-Id` header wiring, the durable outbox's
real HTTP behaviour, and the served-vs-acked reconciliation.

A **native MAUI app cannot be browser-driven** (no Playwright), and driving the XAML via Appium on an
emulator is slow + fragile — so the automated suite is a **headless xUnit harness** that
**source-links the real production services** (`EventStreamingService`, `TelemetryClient`,
`PrintAckOutbox`, `OrderFeedParser`) and runs them against a live backend. The XAML-level UI smoke stays
**manual on the emulator** (the fleet-observability-e2e-plan.md doc) — it exercises MAUI startup + the OS
network stack that the headless harness can't reach.

### No mocks of our own code

The tested services are the **real** shipping classes, source-linked (not re-implemented, not mocked).
The **backend is real** (spun up in CI, or pointed at staging/demo). Following the frontend's rule:
*mock the edge, never our own layers.*

The only substitutions are **edges**, not logic:
- **Config source** — a tiny `IPrinterService` that returns the test `PrinterConfiguration` (the values a
  user would type into Settings). This is data setup, the printer-app equivalent of the frontend's seed.
- **Physical printer** — where a print-to-hardware test is added, a **loopback `TcpListener`** stands in
  for the thermal printer (the unavoidable hardware edge; asserts exact ESC/POS bytes). Reuses the pattern
  in `PrinterAPP.Tests/NetworkTcpTransportTests.cs`.
- **UI log sink** — a no-op `IRequestLogService` (the log is a UI surface, not business logic).

### Direct API calls — only where there's no UI/service path

Per the workspace policy: prefer driving the real service; use a **backend API call only when**
(a) there is no printer-app path for that step — e.g. **creating an order** (the app never creates
orders; the customer frontend does — `POST /api/orders`), or **reading the fleet state** to assert
(`GET /api/devices*`, admin-only — the app never displays it); or (b) the step is already covered via the
real path in another test and re-driving it would only duplicate + slow the run. Never use an API call to
**shortcut the behaviour under test** (e.g. don't POST a heartbeat directly to "prove heartbeats" — run
the real `TelemetryClient`).

## Priority tiering

- **🔴 HIGH (must cover):** feed receives a confirmed order; heartbeat registers the device + reports
  feed state; a print-acked order is **not** flagged missed; a served-but-unacked order **is** flagged
  missed; a device event is retrievable. These are the fleet-observability guarantees.
- **🟡 MED (cover if cheap):** durable outbox retry across a transient failure; zoneless-timestamp
  acceptance; the deser-wedge resilience (one bad order doesn't stall the batch) end-to-end.
- **⚪ LOW (do not add here):** ESC/POS byte formatting (unit-tested in `PrinterAPP.Tests`), every locale,
  UI cosmetics → belong in unit tests or the manual emulator smoke.

**Current suite:** heartbeat, print-ack, and durable-outbox (HIGH/MED telemetry) — verified green against
live staging — **plus the order-feed suite** (`OrderFeedE2ETests`): the real `EventStreamingService`
polling a real backend (positive: a successful poll advances `LastSuccessfulPollAt`; negative: a wrong
`X-Api-Key` never polls + surfaces the auth error; delivery: a Confirmed DineIn order created via
`POST /api/orders` reaches `OrderReceived`). The MAUI-`Color` coupling that blocked source-linking the feed
was removed by moving `LogType`→`Color` off `LogEntry` into a UI converter. **Next iterations:**
(1) **missed-order reconciliation** (create a Confirmed order via `POST /api/orders`, ack one and not the
other, assert `GET /api/devices/missed-orders`) — needs a seeded product catalogue, so it pairs with a
spun-up/seeded backend; (2) **print-to-sink** (`OrderPrintService` → loopback `TcpListener` asserting
ESC/POS) — needs the `PrintStyleSettingsService` injection seam.

> The order-feed delivery test + the negative auth path need a seeded, key-enforcing backend to run for
> real; against a keyless/open or product-less backend they **skip** (never red-fail). The true green pass
> is the `e2e.yml` CI job (which carries the `X-Api-Key` + admin JWT secrets) or a seeded demo backend.

## Environment parameterization (run against any environment)

Every base URL + credential comes from an env var with a safe default, so the same suite runs against a
CI-spun-up backend, staging, or demo by changing env vars only (mirrors the frontend's `E2E_*` pattern):

| Env var | Purpose | Default |
|---|---|---|
| `PRINTERAPP_E2E_API_BASE_URL` | backend under test | `http://localhost:5221` |
| `PRINTERAPP_E2E_API_KEY` | tenant `X-Api-Key` (printer-feed auth) | `""` (open in the test backend) |
| `PRINTERAPP_E2E_ADMIN_JWT` | admin bearer for the `GET /api/devices*` assertions | `""` |
| `PRINTERAPP_E2E_TENANT_SLUG` | slug the device self-reports | `rumi` |

If `PRINTERAPP_E2E_API_BASE_URL` is unreachable the suite **skips** (so it never red-fails a run with no
backend) — CI provides one; a dev exports the vars to point at staging/demo.

## Data isolation

Each test mints a **unique device id** (`e2e-<guid>`) and tags created orders, so parallel runs and reruns
don't collide — the frontend's `e2e-` prefix discipline. Created orders land on a throwaway/CI DB (or
demo, where order pollution is acceptable); nothing is shared beyond the seeded product catalogue.

## CI cadence

`.github/workflows/e2e.yml` — **NOT** on every PR (keeps the fast unit job hermetic and a flaky backend
from blocking merges). Triggers:
- **`schedule`** — weekly (Mon 04:00 UTC) for drift detection.
- **`workflow_dispatch`** — on demand, with an optional `api_base_url` input to retarget any environment.
- Intended to also run **before/after a release** (dispatch it, or call it from the release flow).

The job **points at a deployed backend** (default `https://staging.fooderist.com`, overridable via the
dispatch input / `vars.PRINTERAPP_E2E_API_BASE_URL`), authenticating with `secrets.PRINTERAPP_E2E_API_KEY`
(the tenant `X-Api-Key`) and, for the stronger fleet-read assertions, `secrets.PRINTERAPP_E2E_ADMIN_JWT`.
No emulator, no MAUI workloads — the harness is plain `net10.0`, so the job is just checkout →
`setup-dotnet` → `dotnet test PrinterAPP.E2E`. (Testing the *real* deployed backend is the point for
release/drift checks; a fully hermetic variant that spins up a throwaway backend with `ApiKey=""` — the
frontend-E2E pattern — is a straightforward future addition if PR-gating is ever wanted.)

> **Setup:** set `PRINTERAPP_E2E_API_KEY` as a repo Actions secret to the target backend's printer key
> (rotates with re-provisioning). Without it the auth-gated calls 401 and the run fails — deliberately,
> since it's exercising real auth.

## Adding a scenario — checklist

1. Is it a cross-layer guarantee against the real backend? If it's pure formatting/logic → unit test instead.
2. Drive the **real service**; API-call only for order setup + fleet-state assertions (no app path).
3. Unique `e2e-` device id / order marker; assert, don't depend on other tests' data.
4. Parameterize any new URL/secret via a `PRINTERAPP_E2E_*` env var with a default.
5. Tier it (HIGH/MED); LOW belongs elsewhere.
