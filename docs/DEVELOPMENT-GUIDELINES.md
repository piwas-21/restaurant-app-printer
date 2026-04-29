# Printer-App Development Guidelines

> .NET MAUI 9 | Windows 10+ | ESC/POS Thermal Printers

---

## Architecture Overview

```
PrinterAPP/
  MauiProgram.cs              # DI setup, service registration
  MainPage.xaml / .xaml.cs    # Primary UI + settings management
  OrderManagementPage.xaml/cs # Order history + manual print
  Models/
    Order.cs                  # Order data model (mirrors backend OrderDto)
    OrderItem.cs              # Line items
    IngredientCustomization.cs
    PrinterConfiguration.cs   # Saved settings (JSON)
    PrintStyleSettings.cs     # Font sizes, bold, emphasis
  Services/
    WindowsPrinterService.cs  # Windows Printer API (P/Invoke)
    OrderPrintService.cs      # ESC/POS formatting, receipt composition
    EventStreamingService.cs  # API polling, order event handling
    RequestLogService.cs      # Request/response logging
    UpdateService.cs          # GitHub release auto-update
  Resources/
    Colors.xaml               # Theme colors
```

### Key Patterns
- **Service-oriented** with dependency injection via `MauiProgram.cs`
- **MVVM** with code-behind (standard MAUI pattern)
- **Intelligent printer routing**: Cashier, Front Kitchen, Back Kitchen
- **5-second polling** with 1-hour deduplication window
- **ESC/POS** thermal printer commands with Turkish character support (PC857)

---

## File Length Limits

| File Type | Max LOC | Rationale |
|---|---|---|
| Page code-behind (`.xaml.cs`) | 200 | Business logic in services, not UI code-behind |
| Service class | 300 | Beyond this, split by concern |
| Model class | 80 | Data containers only |
| P/Invoke wrapper | 200 | Isolate native interop in dedicated files |
| Constants file | 100 | ESC/POS commands, config keys |

---

## Naming Conventions

### Files & Classes
- **Services**: `{Feature}Service.cs` with `I{Feature}Service.cs` interface
- **Models**: `{Entity}.cs` - must match backend DTO field names exactly
- **Pages**: `{Name}Page.xaml` + `{Name}Page.xaml.cs`

### Code Style
- PascalCase for public members, `_camelCase` for private fields
- Async methods suffix with `Async`
- All services must have interfaces for testability

---

## Service Interface Requirements

Every service MUST have an interface:

```csharp
// GOOD
public interface IOrderPrintService
{
    Task PrintOrderAsync(Order order, PrinterConfiguration config);
    Task PrintKitchenTicketAsync(Order order, string kitchenType, PrinterConfiguration config);
}

public class OrderPrintService : IOrderPrintService { ... }

// BAD - no interface
public class OrderPrintService { ... }
```

Register all services in `MauiProgram.cs`:
```csharp
builder.Services.AddSingleton<IOrderPrintService, OrderPrintService>();
```

---

## Model Alignment with Backend

Models MUST match backend `OrderDto` field names **exactly** (case-sensitive for JSON deserialization):

```csharp
// GOOD - matches backend DTO
public string MenuId { get; set; }

// BAD - mismatched casing
public string MenuID { get; set; }
```

When the backend adds new fields, update the printer-app model to include them (at minimum as nullable properties to avoid deserialization failures).

---

## ESC/POS Printing

### Command Constants
All ESC/POS commands MUST be defined in a constants file, not inline:

```csharp
// GOOD - in EscPosCommands.cs
public static class EscPosCommands
{
    public static readonly byte[] Initialize = { 0x1B, 0x40 };
    public static readonly byte[] CutPaper = { 0x1D, 0x56, 0x00 };
    public static readonly byte[] BoldOn = { 0x1B, 0x45, 0x01 };
    public static readonly byte[] BoldOff = { 0x1B, 0x45, 0x00 };
    // ...
}

// BAD - inline magic bytes
bytes.AddRange(new byte[] { 0x1B, 0x40 });
```

### Print Routing
- **Cashier printer**: Full receipt with prices, payment details, totals
- **Kitchen printers**: Simple ticket with item name, quantity, customizations
- **Routing logic**: Based on `KitchenType` field - Front Kitchen, Back Kitchen
- **Fallback**: Routes to Cashier printer if target kitchen printer unavailable

### Turkish Characters
- Uses PC857 code page for Turkish character encoding
- Character mapping handled in `OrderPrintService` or `ReceiptFormatter`
- Test with: c, g, i, o, s, u (dotted/undotted variants)

---

## Configuration Management

### Storage Location
- Path: `%APPDATA%\PrinterAPP\`
- Settings: `config.json` (API URL, printer names, paper width, copy counts)
- Print styles: `print_style_settings.json`

### Configuration Fields
```json
{
  "ApiUrl": "https://api.example.com",
  "ApiKey": "...",
  "CashierPrinter": "EPSON TM-T88V",
  "FrontKitchenPrinter": "EPSON TM-T82",
  "BackKitchenPrinter": "",
  "PaperWidth": 80,
  "CashierCopies": 1,
  "KitchenCopies": 1,
  "EnableTimeRestrictions": false,
  "StartTime": "10:00",
  "EndTime": "23:00"
}
```

### Rules
- Validate configuration on load (don't silently use defaults)
- Log configuration errors clearly
- API URL and API Key are required for production

---

## API Integration

### Polling
- Poll `GET /api/orders/printer-feed?modifiedSince={timestamp}` every 5 seconds
- Include `X-Api-Key` header for authentication
- Track processed order IDs with 1-hour expiry to prevent duplicate prints
- Exponential backoff on connection failures

### Response Handling
- Parse `OrderDto` from JSON response
- Handle missing/new fields gracefully (nullable properties)
- Log all API requests and responses via `RequestLogService`

---

## Code-Behind Rules

Page `.xaml.cs` files contain **only UI event handlers**. Business logic goes in services:

```csharp
// GOOD - thin code-behind
private async void OnPrintTestClicked(object sender, EventArgs e)
{
    await _printerTestService.PrintTestPageAsync(_config.CashierPrinter);
}

// BAD - business logic in code-behind
private async void OnPrintTestClicked(object sender, EventArgs e)
{
    var printers = await WindowsPrinterService.GetPrintersAsync();
    var defaultPrinter = printers.FirstOrDefault(p => p.Name == _config.CashierPrinter);
    // ... 50 lines of print logic ...
}
```

---

## Thread Safety

### Lock Pattern
Use `lock` for shared mutable state:

```csharp
private readonly object _lockObject = new();

// GOOD
lock (_lockObject)
{
    _processedOrders[orderNumber] = DateTime.UtcNow;
}
```

### UI Thread Marshaling
Always use `MainThread.BeginInvokeOnMainThread` when updating UI from background threads:

```csharp
// GOOD - marshal to UI thread
MainThread.BeginInvokeOnMainThread(() =>
{
    StatusLabel.Text = "Connected";
    StatusLabel.TextColor = Colors.Green;
});

// BAD - update UI from background thread
StatusLabel.Text = "Connected"; // Crash: cross-thread access
```

### ObservableCollection
`ObservableCollection<T>` is NOT thread-safe. Always modify on UI thread:

```csharp
MainThread.BeginInvokeOnMainThread(() =>
{
    _logs.Insert(0, newEntry);
    if (_logs.Count > MaxEntries) _logs.RemoveAt(_logs.Count - 1);
});
```

---

## Order Deduplication

- Orders tracked by `OrderNumber` in a `Dictionary<string, DateTime>`
- Dedup window: 1 hour (3600 seconds)
- Protected by lock for thread safety
- Cleanup runs on each check (removes entries older than window)
- **Limitation:** Resets on app restart (in-memory only)
- Manual print from UI is NOT deduplicated (intentional)

---

## Turkish Character Encoding

- Uses PC857 code page (Turkish MS-DOS) for thermal printers
- Requires `System.Text.Encoding.CodePages` NuGet package
- ESC/POS command `\x1B\x74\x09` sets printer to PC857
- Supports: c, g, i, o, s, u (dotted/undotted Turkish variants)
- Sent twice for "stubborn printers" in cashier receipt
- Characters not in PC857 are silently dropped

---

## Print Style System

### PrintStyleSettings Model
Defines section styles for kitchen and cashier receipts:
- `SectionStyle` has: `FontSize` (Normal/Tall/Wide/Double/Large), `IsBold`, `IsEmphasized`, `Alignment` (Left/Center/Right)
- Kitchen sections: Header, OrderInfo, OrderType, ItemName, ItemQuantity, Ingredients
- Cashier sections: Header, OrderInfo, ItemLine, Totals, GrandTotal
- Saved to `%APPDATA%\PrinterAPP\print_style_settings.json`
- Converted to ESC/POS bytes via `PrintStyleSettingsService.GetEscPosCommands()`

---

## Auto-Update

- Checks GitHub Releases on startup: `https://api.github.com/repos/mgezgin/PrinterAPP/releases/latest`
- Compares current version (from `.csproj`) with latest release tag
- Selects correct architecture (x64/x86) from release assets
- Downloads to temp file, validates size (>1KB)
- Creates batch script that: waits 3s -> kills process -> copies new exe -> restarts
- Version managed in `PrinterAPP.csproj`: `<Version>1.0.13</Version>`
- **Security note:** No signature verification currently (see SECURITY-AUDIT.md)

---

## Error Handling

### Strategy
- All service methods use try/catch with `ILogger` logging
- User-facing errors shown via `DisplayAlert()` on MainPage
- Background errors logged but don't crash the app
- **Never** expose exception details in user-facing messages

### Exponential Backoff
- API connection failures use exponential backoff: 5s -> 10s -> 20s -> 40s -> 60s (max)
- Resets to 5s on successful connection
- Implemented in `EventStreamingService`

### Retry Pattern
- File operations (update copy) retry up to 2 times
- Print operations retry via polling (next 5s cycle)
- Order deduplication prevents duplicate prints on transient failures

---

## Logging

### ILogger Pattern
```csharp
_logger.LogInformation("Order {OrderNumber} sent to {Printer}", order.OrderNumber, printerName);
_logger.LogWarning("Printer {Name} not found, falling back to cashier", kitchenPrinter);
_logger.LogError(ex, "Failed to print order {OrderNumber}", order.OrderNumber);
```

### Rules
- Use structured logging with named parameters
- **Never log API tokens** or customer PII (names, phones, emails, addresses)
- Log order IDs and totals, not full order JSON
- Remove `System.Diagnostics.Debug.WriteLine()` calls in production builds
- `RequestLogService` keeps max 200 entries in memory (rotates oldest)

---

## Configuration Management

### Storage Locations
- **Config:** `%APPDATA%\KitchenPrinter\config.json` (API URL, printers, settings)
- **Print Styles:** `%APPDATA%\PrinterAPP\print_style_settings.json`
- **Note:** Folder naming inconsistency exists (KitchenPrinter vs PrinterAPP) -- to be unified

### Validation
- Validate configuration on load (don't silently use defaults)
- Reject non-HTTPS API URLs (except localhost for development)
- Validate printer names exist in system before saving

---

## Security

See `docs/SECURITY-AUDIT.md` for full audit (2 CRITICAL, 5 HIGH).

Key rules:
- **Encrypt API token** with DPAPI before storing in config
- **Enforce HTTPS** on API URL configuration
- **Never log PII** (customer names, phones, emails, addresses)
- **Verify update signatures** before installing downloads
- **Validate all API responses** before processing
- **Sanitize printer names** before passing to P/Invoke

---

## Testing

See `docs/TEST-COVERAGE-PLAN.md` for full strategy.

- **No test project currently exists** -- must be created
- Target: 80%+ coverage with 51 tests
- xUnit + Moq + FluentAssertions
- Test critical business logic: deduplication, encoding, routing, pricing
- CI/CD: Tests must run in GitHub Actions before release
