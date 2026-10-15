#!/usr/bin/env bash
set -euo pipefail

# Explicit iOS build: existing Android/Windows jobs retain their current target list.
cd "$(dirname "$0")"
ios_dotnet="${IOS_DOTNET:-dotnet}"
mode="${1:-simulator}"
ios_command=publish
build_args=(PrinterAPP/PrinterAPP.csproj -c Release -f net10.0-ios -p:TargetFrameworks=net10.0-ios --nologo)

case "$mode" in
  simulator)
    ios_command=build
    # Apple Silicon simulators need an ad-hoc signature; no Apple account/profile is needed.
    build_args+=(-p:RuntimeIdentifier=iossimulator-arm64 -p:EnableCodeSigning=true -p:CodesignKey=-)
    ;;
  device-unsigned)
    build_args+=(-p:RuntimeIdentifier=ios-arm64 -p:EnableCodeSigning=false)
    ;;
  device)
    : "${IOS_APPLICATION_ID:?Set the bundle ID that matches your Apple provisioning profile}"
    : "${IOS_CODESIGN_KEY:?Set the Apple signing identity installed in Keychain}"
    : "${IOS_CODESIGN_PROVISION:?Set the installed provisioning profile name or UUID}"
    build_args+=(-p:RuntimeIdentifier=ios-arm64 -p:ArchiveOnBuild=true
      "-p:ApplicationId=$IOS_APPLICATION_ID"
      "-p:CodesignKey=$IOS_CODESIGN_KEY"
      "-p:CodesignProvision=$IOS_CODESIGN_PROVISION")
    ;;
  *)
    echo "Usage: bash build-ios.sh simulator|device-unsigned|device" >&2
    exit 2
    ;;
esac

"$ios_dotnet" "$ios_command" "${build_args[@]}"
if [[ "$mode" != device ]]; then
  echo "$mode build completed; this is not a tenant-installable iPad release."
fi
