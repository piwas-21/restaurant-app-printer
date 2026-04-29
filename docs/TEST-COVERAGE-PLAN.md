# Printer-App Test Coverage Plan

> Target: 80%+ code coverage | Current: 0% (no test project exists)

---

## Current State

**Test Infrastructure: NONE**
- No test project in solution
- No test files anywhere in codebase
- No test frameworks referenced
- No CI/CD test execution

---

## Phase 1: Test Infrastructure Setup

### Create Test Project

```bash
cd printer-app
dotnet new xunit -n PrinterAPP.Tests
dotnet sln PrinterAPP.sln add PrinterAPP.Tests/PrinterAPP.Tests.csproj
dotnet add PrinterAPP.Tests reference PrinterAPP/PrinterAPP.csproj
```

### Add Test Dependencies

```xml
<PackageReference Include="xunit" Version="2.9.3" />
<PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" />
<PackageReference Include="Moq" Version="4.20.70" />
<PackageReference Include="FluentAssertions" Version="7.0.0" />
<PackageReference Include="coverlet.collector" Version="6.0.2" />
```

### Create Mock Helpers

```csharp
// TestHelpers/OrderFactory.cs
public static class OrderFactory
{
    public static Order CreateTestOrder(int orderNumber = 1, string status = "Confirmed") => new()
    {
        OrderNumber = orderNumber,
        Status = status,
        Items = new List<OrderItem> { CreateTestItem() },
        // ...
    };
}
```

---

## Phase 2: Service Unit Tests

### EventStreamingService Tests

| Test | What It Covers |
|------|----------------|
| `DeduplicatesOrderByNumber` | Same order number within 1 hour is skipped |
| `AllowsReprocessAfterWindow` | Order accepted after 1-hour dedup window |
| `CleanupRemovesOldEntries` | Entries older than 1 hour are removed |
| `HandlesInvalidJson` | Malformed API response doesn't crash |
| `HandlesEmptyResponse` | Empty order list handled gracefully |
| `FiltersNonConfirmedOrders` | Only Confirmed status orders processed |
| `ExponentialBackoffOnFailure` | Retry delay doubles from 5s to 60s max |
| `ResetBackoffOnSuccess` | Delay resets to 5s after successful poll |
| `IncludesApiKeyHeader` | X-Api-Key sent when configured |
| `PollsWithModifiedSince` | Timestamp query parameter updated correctly |

**Total: 10 tests**

### OrderPrintService Tests

| Test | What It Covers |
|------|----------------|
| `FormatsCashierReceipt` | Full receipt with header, items, totals, footer |
| `FormatsKitchenTicket` | Simple ticket with items and customizations |
| `RoutesToCorrectPrinter` | FrontKitchen/BackKitchen/Cashier routing |
| `FallsToCashierWhenKitchenMissing` | Missing kitchen printer falls back to cashier |
| `HandlesTurkishCharacters` | Turkish chars encoded in PC857 correctly |
| `AppliesPrintStyles` | Bold, emphasis, font size applied via ESC/POS |
| `HandlesEmptyOrderItems` | Order with no items doesn't crash |
| `CalculatesItemTotals` | Line items formatted with correct prices |
| `PrintsMultipleCopies` | CopiesCount config respected |
| `HandlesLongProductNames` | Names exceeding paper width wrapped |

**Total: 10 tests**

### UpdateService Tests

| Test | What It Covers |
|------|----------------|
| `ComparesVersionsCorrectly` | 1.0.13 < 1.0.14, 1.1.0 > 1.0.99 |
| `DetectsNewerVersion` | Returns update info when newer available |
| `SkipsWhenCurrent` | No update when versions match |
| `SelectsCorrectArchitecture` | x64 on 64-bit OS, x86 on 32-bit |
| `RejectsSmallDownload` | Files under 1KB rejected as corrupt |
| `HandlesGithubApiError` | 404/500 from GitHub handled gracefully |
| `ParsesReleaseNotes` | Release body text extracted correctly |

**Total: 7 tests**

### RequestLogService Tests

| Test | What It Covers |
|------|----------------|
| `CreatesLogEntry` | Log entry with timestamp, type, status |
| `RotatesAt200Entries` | Oldest removed when limit reached |
| `FormatsJsonBody` | JSON pretty-printed in log |
| `HandlesNullBody` | Null response body doesn't crash |

**Total: 4 tests**

### PrintStyleSettingsService Tests

| Test | What It Covers |
|------|----------------|
| `LoadsDefaultStyles` | Returns defaults when no file |
| `SavesAndLoadsStyles` | Round-trip JSON persistence |
| `ConvertsToEscPos` | SectionStyle -> ESC/POS byte commands |
| `CachesSettings` | Multiple reads don't re-read file |
| `InvalidatesOnSave` | Cache cleared after save |

**Total: 5 tests**

### ConfigurationService Tests (after Sprint 7 extraction)

| Test | What It Covers |
|------|----------------|
| `LoadsConfig` | Config loaded from JSON file |
| `SavesConfig` | Config serialized and written |
| `ReturnsDefaultOnMissing` | Default config when file missing |
| `HandlesCorruptFile` | Graceful handling of bad JSON |
| `ValidatesApiUrl` | Rejects non-HTTPS URLs |

**Total: 5 tests**

---

## Phase 3: Integration Tests

| Test | What It Covers |
|------|----------------|
| `ConfigRoundTrip` | Save config -> reload -> values match |
| `PrintStyleRoundTrip` | Save styles -> reload -> values match |
| `OrderDeserializationFromApi` | Real API JSON -> Order model |
| `EscPosEncoding` | Turkish string -> PC857 bytes -> valid |
| `VersionDetectionFromAssembly` | Assembly version matches csproj |

**Total: 5 tests**

---

## Phase 4: Security Tests

| Test | What It Covers |
|------|----------------|
| `RejectsHttpApiUrl` | Non-HTTPS URL configuration rejected |
| `ApiKeyIncludedInRequests` | X-Api-Key header present when configured |
| `PiiNotInLogOutput` | Customer email/phone masked in logs |
| `InvalidJsonDoesNotCrash` | Malformed API response handled |
| `ExtremeQuantityRejected` | Quantity > 999 or < 0 rejected |

**Total: 5 tests**

---

## Coverage Targets

| Service | Current | Target | Tests |
|---------|---------|--------|-------|
| EventStreamingService | 0% | 80% | 10 |
| OrderPrintService | 0% | 80% | 10 |
| UpdateService | 0% | 75% | 7 |
| RequestLogService | 0% | 70% | 4 |
| PrintStyleSettingsService | 0% | 80% | 5 |
| ConfigurationService | 0% | 80% | 5 |
| Integration | 0% | -- | 5 |
| Security | 0% | -- | 5 |
| **Overall** | **0%** | **80%+** | **51** |

---

## CI/CD Integration

Add to GitHub Actions workflow:

```yaml
test:
  runs-on: windows-latest
  steps:
    - uses: actions/checkout@v4
    - uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '9.0.x'
    - run: dotnet test PrinterAPP.Tests --collect:"XPlat Code Coverage"
    - uses: codecov/codecov-action@v4
      with:
        files: '**/coverage.cobertura.xml'
```

---

## Timeline

| Phase | Sprint | Tests | Coverage |
|-------|--------|-------|----------|
| Phase 1 (Setup) | Sprint 7 | 0 | 0% |
| Phase 2 (Service tests) | Sprint 7 | 41 | ~60% |
| Phase 3 (Integration) | Sprint 7 | 5 | ~70% |
| Phase 4 (Security) | Sprint 8 | 5 | **80%+** |
