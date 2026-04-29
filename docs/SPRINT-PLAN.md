# Printer-App Sprint Plan

> Extracted from the RUMI system-wide sprint plan. Printer-app specific tasks only.
> Updated 2026-04-03 with security audit and test coverage findings.

---

## Sprint 1.5: CRITICAL Security Fixes (IMMEDIATE)

> From security audit: 2 CRITICAL + 5 HIGH. See `docs/SECURITY-AUDIT.md`.

### CRITICAL Priority (Block Release)

| # | Task | Severity | File(s) |
|---|------|----------|---------|
| PS1 | **Add update signature verification** - verify Authenticode or hash on downloaded .exe before install | CRITICAL | `Services/UpdateService.cs` |
| PS2 | **Add certificate pinning** - pin backend certificate/public key in HttpClient | CRITICAL | `Services/EventStreamingService.cs` |

### HIGH Priority

| # | Task | Severity | File(s) |
|---|------|----------|---------|
| PS3 | **Encrypt API token with DPAPI** - encrypt before saving to config.json, decrypt on load | HIGH | `Models/PrinterConfiguration.cs`, config save/load code |
| PS4 | **Enforce HTTPS** - validate `ApiBaseUrl` rejects non-HTTPS (allow localhost exception for dev) | HIGH | `Models/PrinterConfiguration.cs` |
| PS5 | **Validate API responses** - check required fields after deserialization, add size limits | HIGH | `Services/EventStreamingService.cs` |
| PS6 | **Sanitize order data** - validate quantities (1-999), prices (>=0), sanitize product names | HIGH | `Services/OrderPrintService.cs` |
| PS7 | **Remove PII from logs** - log only order ID + total, mask customer data | HIGH | `Services/RequestLogService.cs` |

**Acceptance:** Update requires valid signature, MITM on API returns error, API token encrypted in config, HTTP URLs rejected, malformed JSON handled, no PII in log display.

---

## Sprint 7: Refactoring & Modernization

**Goal:** Split god classes, add interfaces, fix model alignment, create test project.

### God Class Decomposition

#### WindowsPrinterService.cs (806 LOC) -> 4 files

| # | Task | New File | Target LOC |
|---|------|----------|------------|
| 7.15a | Extract P/Invoke declarations | `Services/NativePrinterApi.cs` | ~100 |
| 7.15b | Extract printer enumeration | `Services/PrinterDiscoveryService.cs` + interface | ~100 |
| 7.15c | Extract print job management | `Services/RawPrintJobService.cs` + interface | ~150 |
| 7.15d | Slim down orchestrator | `Services/WindowsPrinterService.cs` (modify) | ~200 |

#### OrderPrintService.cs (719 LOC) -> 4 files

| # | Task | New File | Target LOC |
|---|------|----------|------------|
| 7.16a | Extract ESC/POS command constants | `Services/EscPosCommands.cs` | ~40 |
| 7.16b | Extract cashier receipt formatting | `Services/ReceiptFormatter.cs` + interface | ~200 |
| 7.16c | Extract kitchen ticket formatting | `Services/KitchenTicketFormatter.cs` + interface | ~200 |
| 7.16d | Slim down routing logic | `Services/OrderPrintService.cs` (modify) | ~150 |

**Note:** Remove duplicate P/Invoke declarations from OrderPrintService (lines 684-712) - use centralized `NativePrinterApi.cs`.

#### EventStreamingService.cs (689 LOC) -> 3 files

| # | Task | New File | Target LOC |
|---|------|----------|------------|
| 7.17a | Extract polling logic | `Services/OrderPollingService.cs` + interface | ~200 |
| 7.17b | Extract deduplication | `Services/OrderDeduplicationService.cs` + interface | ~100 |
| 7.17c | Slim down event handler | `Services/EventStreamingService.cs` (modify) | ~200 |

#### MainPage.xaml.cs (667 LOC) -> 3 files

| # | Task | New File | Target LOC |
|---|------|----------|------------|
| 7.18a | Extract config management | `Services/ConfigurationService.cs` + interface | ~150 |
| 7.18b | Extract printer test logic | `Services/PrinterTestService.cs` + interface | ~80 |
| 7.18c | Slim down code-behind | `MainPage.xaml.cs` (modify) | ~200 |

### Cross-Cutting Tasks

| # | Task | Details |
|---|------|---------|
| 7.19 | Fix model field name mismatches | Rename `MenuID` -> `MenuId` and others to match backend `OrderDto` |
| 7.20 | Add interfaces to all services | Create `IWindowsPrinterService`, `IOrderPrintService`, `IEventStreamingService`, `IRequestLogService`, `IUpdateService`, register in `MauiProgram.cs` |
| 7.21 | Unify config folder naming | Change `KitchenPrinter` -> `PrinterAPP` (or vice versa) for consistency |
| 7.22 | Remove `System.Diagnostics.Debug.WriteLine` calls | Replace with ILogger or remove |

### Test Project Setup (Sprint 7)

| # | Task |
|---|------|
| T7.1 | Create `PrinterAPP.Tests` xUnit project, add to solution |
| T7.2 | Add Moq, FluentAssertions, coverlet packages |
| T7.3 | Create `OrderFactory` test helper |
| T7.4 | Write EventStreamingService tests (10 tests: dedup, backoff, filtering, JSON handling) |
| T7.5 | Write OrderPrintService tests (10 tests: formatting, routing, encoding, styles) |
| T7.6 | Write UpdateService tests (7 tests: version comparison, download validation, architecture detection) |
| T7.7 | Write RequestLogService tests (4 tests: creation, rotation, formatting) |
| T7.8 | Write PrintStyleSettingsService tests (5 tests: load, save, ESC/POS conversion, cache) |
| T7.9 | Write ConfigurationService tests (5 tests: load, save, defaults, validation) |
| T7.10 | Write integration tests (5 tests: config round-trip, encoding, version detection) |

### Sprint 8 Tests

| # | Task |
|---|------|
| T8.1 | Write security tests (5 tests: HTTPS enforcement, API key header, PII masking, invalid JSON, extreme values) |
| T8.2 | Add test execution to GitHub Actions workflow |

**Acceptance:**
- `dotnet build` passes for app + tests
- All 51 tests pass
- Coverage >= 80%
- Print receipts and kitchen tickets correctly
- 5-second polling with no duplicate prints
- Settings save/load works
- Auto-update check works

---

## All God Classes & Target State

| File | Current LOC | Limit | Target LOC | Sprint |
|------|-------------|-------|------------|--------|
| `WindowsPrinterService.cs` | 806 | 300 | 200 | Sprint 7 |
| `OrderPrintService.cs` | 719 | 300 | 150 | Sprint 7 |
| `EventStreamingService.cs` | 689 | 300 | 200 | Sprint 7 |
| `MainPage.xaml.cs` | 667 | 200 | 200 | Sprint 7 |
| `RequestLogService.cs` | 358 | 300 | 300 | Acceptable |
| `UpdateService.cs` | 331 | 300 | 300 | Acceptable |

---

## New Files Created (Sprint 7)
```
Services/
  NativePrinterApi.cs
  PrinterDiscoveryService.cs + IPrinterDiscoveryService.cs
  RawPrintJobService.cs + IRawPrintJobService.cs
  EscPosCommands.cs
  ReceiptFormatter.cs + IReceiptFormatter.cs
  KitchenTicketFormatter.cs + IKitchenTicketFormatter.cs
  OrderPollingService.cs + IOrderPollingService.cs
  OrderDeduplicationService.cs + IOrderDeduplicationService.cs
  ConfigurationService.cs + IConfigurationService.cs
  PrinterTestService.cs + IPrinterTestService.cs

PrinterAPP.Tests/  (NEW PROJECT)
  TestHelpers/OrderFactory.cs
  Services/EventStreamingServiceTests.cs
  Services/OrderPrintServiceTests.cs
  Services/UpdateServiceTests.cs
  Services/RequestLogServiceTests.cs
  Services/PrintStyleSettingsServiceTests.cs
  Services/ConfigurationServiceTests.cs
  Integration/ConfigIntegrationTests.cs
  Security/SecurityTests.cs
```

---

## Test Coverage Roadmap

| Phase | Sprint | Tests | Coverage |
|-------|--------|-------|----------|
| Phase 1 (Setup) | Sprint 7 | 0 | 0% |
| Phase 2 (Service tests) | Sprint 7 | 41 | ~60% |
| Phase 3 (Integration) | Sprint 7 | 5 | ~70% |
| Phase 4 (Security) | Sprint 8 | 5 | **80%+** |

See `docs/TEST-COVERAGE-PLAN.md` for full details.

---

## Future Considerations

- **SSE vs Polling**: Backend has SSE infrastructure (`OrderEventService`), but printer-app comment says "SSE was unreliable". Investigate reliability or implement webhooks.
- **Shared DTO Package**: Consider `RestaurantSystem.Contracts` NuGet package or OpenAPI-generated client.
- **Persistent Deduplication**: Save processed order IDs to disk to survive app restarts.
- **Config Backup**: Create .bak before overwriting config files.
