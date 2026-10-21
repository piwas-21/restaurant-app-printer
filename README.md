# Rumi Restaurant Printer App

A Windows and Android application built with **.NET MAUI** that handles automated receipt printing for the Rumi Restaurant system. It connects to the backend API, polls for new orders, and routes print jobs to specific thermal printers (Cashier, Front Kitchen, Back Kitchen) with custom formatting.

![Printer App UI](https://raw.githubusercontent.com/mgezgin/PrinterAPP/master/docs/screenshot.png) *(Placeholder if you have one)*

## 🚀 Key Features

- **Auto-Printing:** Automatically polls for "Pending" orders and prints them immediately.
- **Intelligent Routing:**
  - **Cashier Printer:** Prints full bill with prices and payment info.
  - **Kitchen Printers:** Prints simple order tickets.
  - **Fallback Logic:** If "Front Kitchen" printer is missing, routes traffic to the Cashier printer automatically.
- **Smart Formatting:**
  - **Kitchen Tickets:** Daily order number, table, order time, station, dishes, quantities and preparation instructions. Amendment tickets retain the human source-order reference in the order-info style.
  - **Kitchen Corrections:** ADD, CANCEL, CHANGE and instruction changes show only affected preparation; replacement references use the separate ticket number. Copies retain a prominent duplicate warning. Internal UUIDs and job/account revisions are never printed on kitchen tickets; their routing, acknowledgement and history records retain those identities.
  - **Ingredient Customizations:** Kitchen tickets mark added/removed ingredients. Cashier bills can include selected/removed ingredients and sauces with **Include selected/removed ingredients and sauces on cashier bills** in Cashier settings. This preference is saved per printer installation and defaults off; variations have their own switch.
  - **Current cashier bills:** Dashboard requests include all current rounds and committed amendments, including updated selections and removed quantities. The backend must support current-ticket cashier snapshots; existing queued bills retain their original snapshot.
  - **Turish Character Support:** Correctly handles Turkish characters (e.g., `İ`, `Ş`, `Ğ`) on thermal printers.
- **Auto-Update System:**
  - Checks GitHub Releases for updates on startup and via manual check.
  - Automatically downloads and installs new versions.
- **Background Operation:** Runs reliably on Windows to ensure no orders are missed.

## 📥 Installation

1. Go to the [Releases Page](https://github.com/piwas-21/printer-app-releases/releases).
2. Download the latest `PrinterApp-Setup-x64.exe` (or `x86` for older systems).
3. Run the installer.
4. Launch "PrinterAPP" from the Start Menu.

## ⚙️ Configuration

On first launch, you must configure the application:

1. **API URL:** Use this tenant’s current HTTPS backend base URL, matching the existing installation configuration.
2. **Printers:** Select the Windows printer queues for:
   - **Cashier Printer** (Main receipt printer)
   - **Kitchen Printer** (Back of house)
   - **Front Kitchen Printer** (optional - Bar/Front prep area)
3. **Auto-Print:** Enable the toggle to start polling for orders.

## 🛠️ Development

### Prerequisites
- **Windows** for the Windows target
- **.NET 10 SDK**
- **MAUI Workload** (`dotnet workload install maui`)

### Building Locally

```powershell
# Restore dependencies
dotnet restore

# Run in Debug mode
dotnet build -c Debug -t:Run -f net10.0-windows10.0.19041.0
```

### Publishing

To build the standalone `.exe`:

```powershell
dotnet publish PrinterAPP/PrinterAPP.csproj -c Release -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained -p:PublishSingleFile=true
```

## 📦 Release Workflow

We use automated GitHub Actions for releases.

👉 **See [RELEASE_WORKFLOW.md](RELEASE_WORKFLOW.md) for detailed release instructions.**

### Quick Summary
1. Update `<Version>`, `<ApplicationDisplayVersion>`, and `<ApplicationVersion>` in `PrinterAPP.csproj`.
2. Follow [RELEASE_WORKFLOW.md](RELEASE_WORKFLOW.md) for the develop PR, platform builds, production release, and tag sequence.

## 📄 License

Proprietary software for Rumi Restaurant.
