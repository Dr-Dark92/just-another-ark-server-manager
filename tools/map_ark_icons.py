#!/usr/bin/env python3
"""
Map a locally downloaded ARK icon snapshot into JAASM's bundled offline catalogue.

No network access is used. The source directory should contain image files saved
from the ARK wiki or another permitted source. Matching is based on the canonical
display names already stored in ark-items.json / ark-engrams.json.

Example:
  python tools/map_ark_icons.py --source D:\ARK-Wiki-Icons

Outputs:
  src/JAASM.App/Data/Icons/items/
  src/JAASM.App/Data/Icons/engrams/

The JSON iconFile fields are already canonical:
  Icons/items/<Display Name>.png
  Icons/engrams/<Display Name>.png
"""

from __future__ import annotations

import argparse
import json
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "src" / "JAASM.App" / "Data"
ITEM_JSON = DATA / "ark-items.json"
ENGRAM_JSON = DATA / "ark-engrams.json"
ITEM_OUT = DATA / "Icons" / "items"
ENGRAM_OUT = DATA / "Icons" / "engrams"

EXTS = (".png", ".webp", ".jpg", ".jpeg")


def key(value: str) -> str:
    return "".join(ch.lower() for ch in value if ch.isalnum())


def build_source_index(source: Path) -> dict[str, Path]:
    index: dict[str, Path] = {}
    for path in source.rglob("*"):
        if not path.is_file() or path.suffix.lower() not in EXTS:
            continue

        variants = {
            key(path.stem),
            key(path.stem.replace("_", " ")),
            key(path.stem.replace("-", " ")),
        }
        for variant in variants:
            if variant:
                index.setdefault(variant, path)
    return index


def copy_png_compatible(src: Path, dest: Path) -> bool:
    # JAASM maps filenames as .png. Only copy actual PNGs directly; other image
    # formats are reported so they can be normalized once rather than mislabeled.
    if src.suffix.lower() != ".png":
        return False

    dest.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(src, dest)
    return True


def map_catalog(
    entries: list[dict],
    source_index: dict[str, Path],
    output_root: Path,
) -> tuple[int, list[str], list[str]]:
    mapped = 0
    missing: list[str] = []
    conversion_needed: list[str] = []

    for entry in entries:
        name = entry.get("displayName", "").strip()
        if not name:
            continue

        candidates = [
            key(name),
            key(name + " icon"),
            key(name.replace("'", "")),
        ]

        src = next((source_index.get(candidate) for candidate in candidates
                    if source_index.get(candidate)), None)

        if src is None:
            missing.append(name)
            continue

        dest = output_root / f"{name}.png"
        if copy_png_compatible(src, dest):
            mapped += 1
        else:
            conversion_needed.append(f"{name} <- {src.name}")

    return mapped, missing, conversion_needed


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, help="Local folder containing ARK icon files")
    parser.add_argument("--strict", action="store_true", help="Return nonzero when mappings are missing")
    args = parser.parse_args()

    source = Path(args.source)
    if not source.is_dir():
        raise SystemExit(f"Icon source directory not found: {source}")

    item_doc = json.loads(ITEM_JSON.read_text(encoding="utf-8"))
    engram_doc = json.loads(ENGRAM_JSON.read_text(encoding="utf-8"))
    index = build_source_index(source)

    item_mapped, item_missing, item_convert = map_catalog(
        item_doc.get("items", []), index, ITEM_OUT
    )
    engram_mapped, engram_missing, engram_convert = map_catalog(
        engram_doc.get("engrams", []), index, ENGRAM_OUT
    )

    print(f"Items:   mapped={item_mapped} missing={len(item_missing)}")
    print(f"Engrams: mapped={engram_mapped} missing={len(engram_missing)}")

    if item_convert or engram_convert:
        print("\nNon-PNG files requiring normalization:")
        for value in item_convert + engram_convert:
            print(f"  {value}")

    if item_missing:
        print("\nMissing item icons:")
        for value in item_missing:
            print(f"  {value}")

    if engram_missing:
        print("\nMissing engram icons:")
        for value in engram_missing:
            print(f"  {value}")

    if args.strict and (item_missing or engram_missing or item_convert or engram_convert):
        return 2

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
