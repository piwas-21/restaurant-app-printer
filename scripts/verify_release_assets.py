#!/usr/bin/env python3
"""Fail closed unless the complete production release asset set is present."""

from __future__ import annotations

import argparse
from pathlib import Path


EXPECTED_ASSETS = {
    "PrinterApp-Android.apk",
    "PrinterApp-Setup-x64.exe",
    "PrinterApp-Setup-x86.exe",
}


def validate_release_assets(directory: Path) -> tuple[Path, ...]:
    if directory.is_symlink() or not directory.is_dir():
        raise ValueError("release asset path must be a real directory")

    entries = list(directory.iterdir())
    names = {entry.name for entry in entries}
    if names != EXPECTED_ASSETS or len(entries) != len(EXPECTED_ASSETS):
        expected = ", ".join(sorted(EXPECTED_ASSETS))
        found = ", ".join(sorted(names)) or "<empty>"
        raise ValueError(f"release asset set mismatch; expected [{expected}], found [{found}]")

    assets: list[Path] = []
    for entry in entries:
        if entry.is_symlink() or not entry.is_file() or entry.stat().st_size == 0:
            raise ValueError(f"release asset must be a non-empty regular file: {entry.name}")
        assets.append(entry)

    return tuple(sorted(assets))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path, help="directory containing release artifacts")
    args = parser.parse_args()

    try:
        assets = validate_release_assets(args.directory)
    except (OSError, ValueError) as error:
        parser.error(str(error))

    print(f"Validated complete release asset set ({len(assets)} files).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
