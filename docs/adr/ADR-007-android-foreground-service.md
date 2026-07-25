# ADR-007 — Android background execution via a specialUse foreground service

**Status:** Accepted
**Date:** 2026-07-25
**Author:** mahmutkaya
**Reviewers:** mahmutkaya
**Implements / supersedes:** RUMI client report — "the app doesn't work on minimise or when opening another app"
**References:**
- `PrinterAPP/Platforms/Android/OrderFeedForegroundService.cs`
- `PrinterAPP/Services/IOrderPipeline.cs`, `PrinterAPP/Services/OrderPipeline.cs`
- `PrinterAPP/Platforms/Android/AndroidManifest.xml`
- [ADR-004](ADR-004-github-release-auto-update.md) — the sideload distribution channel this decision depends on
- [ADR-005](ADR-005-multi-target-maui-android.md) — the Android target this extends

---

## Context

RUMI (the live tenant) reported that the Android printer-app stops working when it is minimised or
another app is opened: orders stop printing until staff bring it back to the foreground.

The app had **no Android background-execution mechanism at all**. The whole order path was a plain
`Task` loop owned by a UI page:

- `EventStreamingService.PollForOrdersAsync` — a `while` loop with `Task.Delay(5s)`, started from `MainPage`.
- `MainPage`'s constructor subscribed `OrderReceived` and printed inside `MainThread.BeginInvokeOnMainThread`.
- `TelemetryScheduler` — also started from `MainPage.InitializeAsync`.

The manifest declared only `INTERNET`, `ACCESS_NETWORK_STATE` and `REQUEST_INSTALL_PACKAGES`.

So once the Activity stopped, Android treated the process as a cached process: App Standby and Doze
throttle its network and defer its timers, and the low-memory killer reclaims it. There is no crash
and no Sentry event — the process simply ceases, which is why the failure was invisible in
telemetry. OEM battery managers (Samsung, Xiaomi, Huawei, Oppo — common on inexpensive restaurant
tablets) freeze such processes within minutes regardless of memory pressure.

Two constraints shaped the choice of foreground-service type:

1. Android 14 (API 34) requires every foreground service to declare a **type**, and to pass it again
   at `startForeground()` time or the platform throws `MissingForegroundServiceTypeException`.
2. Android 15 (API 35) caps `dataSync` foreground services at **6 hours per rolling 24 hours**. The
   app currently builds against `targetSdkVersion=36`.

A restaurant's service day exceeds 6 hours, and the timeout arrives silently mid-service — the exact
failure mode we are trying to eliminate.

## Decision

**We will run the order feed inside an Android foreground service declared with the `specialUse`
type, and we will make the order pipeline headless so it does not depend on any UI.**

Concretely:

- `IOrderPipeline` / `OrderPipeline` own feed → print → history → print-ack, registered as a DI
  singleton and driven with no page, window or Activity. `MainPage` is reduced to status display and
  the Start/Stop toggle.
- `OrderFeedForegroundService` hosts the pipeline, returns `START_STICKY`, holds a
  `PARTIAL_WAKE_LOCK`, and shows a low-importance ongoing notification that doubles as the
  staff-visible health indicator.
- `BootReceiver` restarts the service on `BOOT_COMPLETED` / `QUICKBOOT_POWERON`.
- `IBackgroundRunner` is the platform seam: `AndroidBackgroundRunner` starts the service;
  `DirectBackgroundRunner` (Windows and any future head) drives the pipeline in-process, because a
  minimised desktop process is not frozen or reclaimed.

The `specialUse` justification is declared in `AndroidManifest.xml` via
`PROPERTY_SPECIAL_USE_FGS_SUBTYPE`. It has no runtime effect; it exists for a future Play submission.

## Consequences

### Positive
- Orders keep printing when the app is minimised, when another app is in front, with the screen off,
  and in deep Doze. Verified on an Android 16 (API 36) emulator with **no** battery-optimisation
  whitelisting: 18/18 expected polls across 90s of forced deep idle.
- Survives process death (`START_STICKY`) and device reboot (`BootReceiver`) with no UI involvement —
  which is only possible because the pipeline is headless.
- A process hosting a foreground service is exempt from ordinary background reclamation; `am kill`
  could not touch it during verification.
- The persistent notification gives staff and support a zero-effort way to see whether the app is
  alive, without unlocking it.
- Removing the print path from `MainPage` cuts a chunk out of a file that is ~780 LOC against a
  200-LOC limit (baselined debt).

### Negative
- A permanent notification the staff cannot dismiss. Unavoidable — it is the price of a foreground service.
- A partial wake lock keeps the CPU awake continuously, which costs battery. Acceptable only because
  these are fixed, mains-powered kitchen tablets; it would be the wrong trade on a phone.
- `specialUse` is the type most likely to be challenged if the app is ever submitted to Google Play
  (it requires a review form and can be rejected).
- The service holds the process alive, so a memory leak now has a much longer window to matter.

### Mitigation for the negatives
- The notification is `IMPORTANCE_LOW` and silent, and it carries useful state (last successful poll)
  rather than being pure noise.
- If the app goes to Play, switch to `connectedDevice` — honest for an app holding a TCP link to a
  network thermal printer, not subject to the 6-hour cap, and Play-safe. It requires a prerequisite
  permission such as `CHANGE_WIFI_STATE`. That switch is a manifest + `startForeground` change only;
  nothing in the pipeline depends on the type.
- OEM battery managers can still freeze the service. A battery-optimisation exemption prompt and a
  background-health panel are tracked as the follow-up PR; the on-site checklist is in the
  cross-platform plan.

## Alternatives considered

### Alternative A: `dataSync` foreground service
The conventional type for "uploading or downloading data", and the one requiring no justification
property. Rejected because Android 15+ caps it at 6 hours per 24 hours for apps targeting SDK 35+;
the service would die partway through a service day and the `onTimeout()` callback gives seconds to
comply. Pinning `targetSdkVersion` to 34 would dodge the cap but forfeits platform hardening and
blocks any future Play submission, so it was rejected as a can-kick.

### Alternative B: `connectedDevice` foreground service
No daily cap, Play-safe, and defensible — the app maintains a TCP connection to a physical network
printer. Rejected *for now* only because Android 14 gates the type behind a prerequisite permission
(`CHANGE_WIFI_STATE`, `BLUETOOTH_CONNECT`, …) that we would be declaring to satisfy a gate rather
than because we use it. Documented above as the migration path if the app is ever published to Play.

### Alternative C: `WorkManager` periodic work
The standard "correct" answer for deferrable background work. Rejected: the minimum periodic interval
is 15 minutes and execution is deliberately batched and deferred. Kitchen tickets must print within
seconds of an order being confirmed, so deferrable scheduling is the wrong primitive entirely.

### Alternative D: Push (FCM) instead of polling
A high-priority FCM message can wake the app and is exempt from the background-start restrictions,
which would remove the need for a permanently running service. Rejected for this change: it requires
backend work, a Firebase project and per-device token registration, and it replaces a mechanism that
already works with one that depends on Google Play Services being present on the tablet. Worth
revisiting if battery or OEM freezing proves to be a problem in the field.
