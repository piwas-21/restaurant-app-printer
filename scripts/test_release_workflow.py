"""Regression controls for the fail-closed release asset manifest check."""

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from verify_release_assets import EXPECTED_ASSETS, validate_release_assets

REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
WORKFLOW_PATH = REPOSITORY_ROOT / ".github/workflows/build-release.yml"


def assert_release_workflow_contract(workflow: str) -> None:
    windows_start = workflow.index("  build-windows:\n")
    android_start = workflow.index("  build-android:\n")
    publish_start = workflow.index("  publish-release:\n")
    windows_job = workflow[windows_start:android_start]
    android_job = workflow[android_start:publish_start]
    publish_job = workflow[publish_start:]
    signing_start = android_job.index(
        "    - name: Require and decode Android release signing material\n"
    )
    apk_build_start = android_job.index("    - name: Publish release-signed APK\n")
    signing_step = android_job[signing_start:apk_build_start]

    if "rid: [win-x64, win-x86]" not in windows_job:
        raise AssertionError("both Windows architectures must be built")
    if "needs: [build-windows, build-android]" not in publish_job:
        raise AssertionError("publication must depend on both platform build jobs")
    if workflow.count("softprops/action-gh-release@") != 1:
        raise AssertionError("release assets must use one aggregate publication action")
    if "python3 scripts/verify_release_assets.py release-assets" not in publish_job:
        raise AssertionError("publication must validate the complete asset set first")
    checkout_index = publish_job.find(
        "uses: actions/checkout@9c091bb21b7c1c1d1991bb908d89e4e9dddfe3e0"
    )
    validator_index = publish_job.find("python3 scripts/verify_release_assets.py release-assets")
    if checkout_index < 0 or checkout_index > validator_index:
        raise AssertionError("the publisher must check out the validation script before using it")
    if "always()" in publish_job:
        raise AssertionError("publication must retain default success-only job semantics")

    required_secrets = {
        "ANDROID_KEYSTORE_BASE64",
        "ANDROID_KEYSTORE_PASSWORD",
        "ANDROID_KEY_ALIAS",
        "ANDROID_KEY_PASSWORD",
    }
    if not all(f"${{{{ secrets.{name} }}}}" in signing_step for name in required_secrets):
        raise AssertionError("all Android release signing secrets must be wired into the build")
    if "if [[ -z \"${!secret_name}\" ]]" not in signing_step:
        raise AssertionError("the Android signing step must reject every missing secret")
    if "-c Release -f net10.0-android" not in android_job:
        raise AssertionError("Android release packaging must use the Release configuration")
    if "-p:AndroidKeyStore=true" not in android_job:
        raise AssertionError("Android release packaging must enable keystore signing")
    if "Build Debug APK" in android_job or "-c Debug" in android_job:
        raise AssertionError("a Debug APK must never be an eligible release artifact")
    if "-name '*-Signed.apk'" not in android_job or "${#signed_apks[@]} -ne 1" not in android_job:
        raise AssertionError("the Android job must require exactly one signed APK")

    expected_names = {
        "PrinterApp-Setup-x64.exe",
        "PrinterApp-Setup-x86.exe",
        "PrinterApp-Android.apk",
    }
    if not all(name in publish_job for name in expected_names):
        raise AssertionError("the publisher must upload the complete expected asset set")


class ReleaseAssetValidationTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.directory = Path(self.temp_dir.name) / "assets"
        self.directory.mkdir()

    def tearDown(self) -> None:
        self.temp_dir.cleanup()

    def add_complete_set(self) -> None:
        for name in EXPECTED_ASSETS:
            (self.directory / name).write_bytes(b"release artifact")

    def test_accepts_exact_nonempty_three_asset_set(self) -> None:
        self.add_complete_set()

        assets = validate_release_assets(self.directory)

        self.assertEqual({asset.name for asset in assets}, EXPECTED_ASSETS)

    def test_rejects_each_missing_required_asset(self) -> None:
        for missing in EXPECTED_ASSETS:
            with self.subTest(missing=missing):
                for child in self.directory.iterdir():
                    child.unlink()
                self.add_complete_set()
                (self.directory / missing).unlink()

                with self.assertRaisesRegex(ValueError, "asset set mismatch"):
                    validate_release_assets(self.directory)

    def test_rejects_an_extra_asset(self) -> None:
        self.add_complete_set()
        (self.directory / "unexpected.apk").write_bytes(b"extra")

        with self.assertRaisesRegex(ValueError, "asset set mismatch"):
            validate_release_assets(self.directory)

    def test_rejects_empty_asset(self) -> None:
        self.add_complete_set()
        (self.directory / "PrinterApp-Android.apk").write_bytes(b"")

        with self.assertRaisesRegex(ValueError, "non-empty regular file"):
            validate_release_assets(self.directory)

    def test_rejects_directory_instead_of_asset(self) -> None:
        self.add_complete_set()
        (self.directory / "PrinterApp-Android.apk").unlink()
        (self.directory / "PrinterApp-Android.apk").mkdir()

        with self.assertRaisesRegex(ValueError, "non-empty regular file"):
            validate_release_assets(self.directory)

    def test_rejects_symlink_asset(self) -> None:
        self.add_complete_set()
        android_apk = self.directory / "PrinterApp-Android.apk"
        android_apk.unlink()
        target = self.directory.parent / "actual.apk"
        target.write_bytes(b"release artifact")
        android_apk.symlink_to(target.name)

        with self.assertRaisesRegex(ValueError, "non-empty regular file"):
            validate_release_assets(self.directory)

    def test_rejects_symlinked_asset_directory(self) -> None:
        self.add_complete_set()
        link = self.directory.parent / "asset-link"
        link.symlink_to(self.directory, target_is_directory=True)

        with self.assertRaisesRegex(ValueError, "real directory"):
            validate_release_assets(link)


class ReleaseWorkflowContractTests(unittest.TestCase):
    def setUp(self) -> None:
        self.workflow = WORKFLOW_PATH.read_text(encoding="utf-8")

    def test_current_workflow_is_fail_closed(self) -> None:
        assert_release_workflow_contract(self.workflow)

    def test_contract_rejects_partial_windows_matrix(self) -> None:
        changed = self.workflow.replace(
            "rid: [win-x64, win-x86]", "rid: [win-x64]", 1
        )

        with self.assertRaisesRegex(AssertionError, "both Windows architectures"):
            assert_release_workflow_contract(changed)

    def test_contract_rejects_publisher_without_both_build_dependencies(self) -> None:
        changed = self.workflow.replace(
            "needs: [build-windows, build-android]", "needs: build-android", 1
        )

        with self.assertRaisesRegex(AssertionError, "both platform build jobs"):
            assert_release_workflow_contract(changed)

    def test_contract_rejects_missing_publisher_checkout(self) -> None:
        changed = self.workflow.replace(
            "    - name: Checkout source for release asset validation\n"
            "      uses: actions/checkout@9c091bb21b7c1c1d1991bb908d89e4e9dddfe3e0 # v7.0.0\n\n",
            "",
            1,
        )

        with self.assertRaisesRegex(AssertionError, "check out the validation script"):
            assert_release_workflow_contract(changed)

    def test_contract_rejects_missing_signing_secret(self) -> None:
        changed = self.workflow.replace(
            "ANDROID_KEY_PASSWORD: ${{ secrets.ANDROID_KEY_PASSWORD }}\n", "", 1
        )

        with self.assertRaisesRegex(AssertionError, "all Android release signing secrets"):
            assert_release_workflow_contract(changed)

    def test_contract_rejects_debug_release_fallback(self) -> None:
        changed = self.workflow.replace(
            "    - name: Collect exactly one signed APK\n",
            "    - name: Build Debug APK\n      run: dotnet publish -c Debug\n\n"
            "    - name: Collect exactly one signed APK\n",
            1,
        )

        with self.assertRaisesRegex(AssertionError, "Debug APK"):
            assert_release_workflow_contract(changed)


if __name__ == "__main__":
    unittest.main()
