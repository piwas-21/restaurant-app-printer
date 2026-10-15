# iOS foreground printer pilot

The pilot supports a dedicated iPad/iPhone running the app in the foreground, with reachable LAN
printers that accept raw ESC/POS over TCP. Enter each printer's IP address and optional port in
settings; the normal raw-print port is 9100. No USB/Bluetooth compatibility is claimed.
The optional saved `FeedProbeTimeoutSeconds` setting controls the API Test timeout (default 10 seconds).

## Current installation gate

No paid Apple Developer account or signing identity is available for the requested tenant onboarding.
The printer model/connection is also unconfirmed. Source and simulator builds do not
constitute a tenant-installable release. The tenant has no Windows PC or Android print station.
Manual browser printing may cover onboarding if its printer supports AirPrint; the model and iPad
print flow must be verified first. Remote installation of a properly signed build does not require
a tenant Mac.

## Build prerequisites

Use a Mac with Xcode 27.0 and .NET SDK 10.0.401. Install the matching workload in that SDK:

```bash
dotnet workload install maui-ios --version 10.0.401.1
bash build-ios.sh simulator
```

The simulator build uses an ad-hoc signature without Apple credentials; it runs only in Simulator.
An isolated SDK can be selected with `IOS_DOTNET=/absolute/path/to/dotnet`. This avoids changing
the toolchain used by other checkouts. The iOS build is explicit and does not add an Apple workload
requirement to the existing Android/Windows target list.

`bash build-ios.sh device-unsigned` verifies the arm64 device build. Neither simulator nor unsigned-device mode produces
an app that may be installed on a physical tenant device.

## Signed device build

Enroll in the Apple Developer Program. Register the bundle ID and the iPad UDID, create the
appropriate development/ad-hoc profile, and install the signing identity and profile on the build
Mac. Keep certificates, private keys and provisioning profiles outside the repository.

Set these local environment variables to values from that account:

- `IOS_APPLICATION_ID`: the bundle ID covered by the profile.
- `IOS_CODESIGN_KEY`: the installed Apple signing identity.
- `IOS_CODESIGN_PROVISION`: the profile name or UUID.

Then run `bash build-ios.sh device`. The signed `.ipa` is produced under
`PrinterAPP/bin/Release/net10.0-ios/ios-arm64/publish/`. Install using the profile's permitted
distribution route (registered-device installation, or upload an appropriately provisioned build
to TestFlight). TestFlight external testing can require review; it is not a guaranteed next-day path.

Apple's free Personal Team is for personal on-device development testing and expires after seven
days. It is not a substitute for ongoing tenant distribution.

## Tenant setup and verification

1. Confirm the printer model supports raw network ESC/POS. Confirm the iPad can reach its LAN IP.
2. Install the signed app, set the tenant API URL/token, restaurant details and printer IPs, and save.
3. Grant the iOS local-network permission and run **Test Print**. If denied, enable Local Network for
   the app in iPad Settings and retry. This permission is for printer access, not the internet feed.
4. Select the kitchen routing mode and confirm cashier/kitchen destinations using the sink/emulator
   verification workflow. Keep only one station owning each destination during the pilot.
5. Start the service and verify feed → receipt output → acknowledgment, then verify saved settings
   and token persistence after relaunch.

The iOS UI omits Windows printer queues and the GitHub Update button. The app keeps the foreground
screen awake. Updates require a newly signed build through the chosen Apple distribution channel.

References: [Apple registered-device distribution](https://developer.apple.com/documentation/xcode/distributing-your-app-to-registered-devices),
[membership and free signing limits](https://developer.apple.com/support/compare-memberships/),
[MAUI publishing](https://learn.microsoft.com/en-us/dotnet/maui/ios/deployment/publish-cli?view=net-maui-10.0).
