# ADR-009 — iOS foreground network-printer pilot

**Status:** Proposed
**Date:** 2026-10-10
**Supersedes:** ADR-005's deferral of the iOS target, for this pilot scope only.

## Context

A tenant needs iPad printing for onboarding. The existing app can reuse its HTTP feed, ESC/POS
composition, network transport, routing and persistence. The owner currently has no paid Apple
Developer account; the tenant has no Mac and its printer model/connection is unconfirmed.

## Decision

- Build `net10.0-ios` explicitly through `build-ios.sh`, leaving the default Android/Windows target
  list intact. Use .NET SDK 10.0.401, workload set 10.0.401.1 and Xcode 27.0. The iOS target alone
  uses MAUI Controls 10.0.110, as recommended by the matching .NET iOS release.
- Scope the pilot to a foreground app with network ESC/POS printers. Keep the screen awake when
  the iOS window is activated. No new background-service architecture is introduced.
- Register `NetworkPrinterService`, hide Windows queue controls, and validate persisted printer
  targets as network IPs or diagnostic file sinks. Do not compile the Windows printer service or
  its GitHub installer updater into the iOS app.
- Register `AppleUpdateService`. It cannot download/install GitHub assets. iOS updates are signed
  builds distributed through Apple provisioning, TestFlight or the App Store.
- Keep existing backend contracts and API-key custody. Add a local-network usage description.
- Compile a Release simulator app with ad-hoc signing in a separate workflow. Device builds require
  an installed Apple identity, provisioning profile and matching bundle ID supplied externally.

## Consequences

The pilot can be compiled and tested without signing credentials, but an unsigned `.app` or `.ipa`
is not installable on a tenant iPad. A device UDID alone is insufficient. Apple permits free Personal
Team signing for personal on-device development testing; its profiles expire after seven days.
That is not this tenant's supported deployment path. Tenant deployment needs paid enrollment,
signing and provisioning, followed by device and printer acceptance.

Android and Windows use their existing dependencies, printer service and update channel. The
new shared connection-probe utility preserves the existing feed request and API-key header.

See [iOS pilot installation](../IOS-PILOT.md),
[Apple membership](https://developer.apple.com/support/compare-memberships/), and
[the matching .NET iOS release](https://github.com/dotnet/macios/releases/tag/dotnet-10.0.1xx-xcode27.0-10722).
