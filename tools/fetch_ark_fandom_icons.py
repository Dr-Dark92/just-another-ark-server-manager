#!/usr/bin/env python3
"""
Fetch ARK item/engram icons from the public ARK Fandom wiki and bundle them
into JAASM's local Data/Icons tree.

This is a BUILD-TIME asset sync. JAASM itself never contacts the wiki at runtime.

No MediaWiki API is used. The fetcher uses normal public wiki/file URLs and
Fandom's Special:Redirect/file endpoint, which redirects to the published asset.

Usage:
  python tools/fetch_ark_fandom_icons.py
  python tools/fetch_ark_fandom_icons.py --force
  python tools/fetch_ark_fandom_icons.py --strict

Outputs:
  src/JAASM.App/Data/Icons/items/*.png
  src/JAASM.App/Data/Icons/engrams/*.png
  src/JAASM.App/Data/icon-sync-report.json
"""

from __future__ import annotations

import argparse
import json
import re
import ssl
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "src" / "JAASM.App" / "Data"
ITEM_JSON = DATA / "ark-items.json"
ENGRAM_JSON = DATA / "ark-engrams.json"
REPORT_JSON = DATA / "icon-sync-report.json"

BASE = "https://ark.fandom.com/wiki/Special:Redirect/file/"
USER_AGENT = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
    "AppleWebKit/537.36 (KHTML, like Gecko) "
    "Chrome/152.0.0.0 Safari/537.36"
)

IMAGE_MAGIC = (
    b"\x89PNG\r\n\x1a\n",
    b"RIFF",
    b"\xff\xd8\xff",
    b"GIF87a",
    b"GIF89a",
)


def safe_name(value: str) -> str:
    return re.sub(r'[\\/:*?"<>|]', "_", value).strip()


def file_slug(value: str) -> str:
    value = value.replace("’", "'").strip()
    value = value.replace(" ", "_")
    return urllib.parse.quote(value, safe="_-'()")


def is_image(data: bytes, content_type: str) -> bool:
    ctype = (content_type or "").lower()
    if ctype.startswith("image/"):
        return True
    return any(data.startswith(magic) for magic in IMAGE_MAGIC)


def candidate_file_names(display_name: str, class_name: str) -> list[str]:
    names: list[str] = []

    def add(value: str) -> None:
        value = value.strip()
        if value and value not in names:
            names.append(value)

    # ARK/Fandom commonly uses human-readable page/item names as icon filenames.
    add(f"{display_name}.png")
    add(f"{display_name.replace(' ', '_')}.png")

    # A few wiki assets are suffixed "_Icon".
    add(f"{display_name}_Icon.png")
    add(f"{display_name.replace(' ', '_')}_Icon.png")

    # Last-resort class-derived candidates.
    base = class_name
    base = re.sub(r"^PrimalItem(?:Resource|Consumable|Weapon|Armor)?_", "", base)
    base = re.sub(r"^EngramEntry_", "", base)
    base = re.sub(r"_C$", "", base)
    if base:
        add(f"{base}.png")
        add(f"{base}_Icon.png")

    return names


def fetch_candidate(filename: str, timeout: int = 30) -> tuple[bytes, str, str] | None:
    url = BASE + file_slug(filename)
    request = urllib.request.Request(
        url,
        headers={
            "User-Agent": USER_AGENT,
            "Accept": "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8",
            "Referer": "https://ark.fandom.com/",
        },
    )

    context = ssl.create_default_context()

    try:
        with urllib.request.urlopen(request, timeout=timeout, context=context) as response:
            data = response.read()
            content_type = response.headers.get("Content-Type", "")
            final_url = response.geturl()

            if not data or not is_image(data, content_type):
                return None

            return data, final_url, content_type
    except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError, ValueError):
        return None


def sync_entry(
    entry: dict,
    icon_kind: str,
    force: bool,
) -> dict:
    display_name = str(entry.get("displayName", "")).strip()
    class_name = str(entry.get("className", "")).strip()
    icon_file = str(entry.get("iconFile", "")).strip()

    if not icon_file:
        icon_file = f"Icons/{icon_kind}/{safe_name(display_name)}.png"
        entry["iconFile"] = icon_file

    target = DATA / icon_file

    if target.exists() and target.stat().st_size > 32 and not force:
        return {
            "name": display_name,
            "className": class_name,
            "status": "cached",
            "iconFile": icon_file,
        }

    target.parent.mkdir(parents=True, exist_ok=True)

    for candidate in candidate_file_names(display_name, class_name):
        fetched = fetch_candidate(candidate)
        if fetched is None:
            continue

        data, final_url, content_type = fetched
        target.write_bytes(data)

        entry["sourceUrl"] = final_url

        return {
            "name": display_name,
            "className": class_name,
            "status": "downloaded",
            "candidate": candidate,
            "iconFile": icon_file,
            "bytes": len(data),
            "contentType": content_type,
            "sourceUrl": final_url,
        }

    return {
        "name": display_name,
        "className": class_name,
        "status": "missing",
        "iconFile": icon_file,
        "candidates": candidate_file_names(display_name, class_name),
    }


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def save_json(path: Path, payload: dict) -> None:
    path.write_text(
        json.dumps(payload, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--force", action="store_true", help="Re-download existing icons")
    parser.add_argument(
        "--strict",
        action="store_true",
        help="Exit non-zero if any mapped catalogue icon could not be downloaded",
    )
    parser.add_argument(
        "--delay",
        type=float,
        default=0.08,
        help="Delay between entries to avoid hammering the wiki",
    )
    args = parser.parse_args()

    items = load_json(ITEM_JSON)
    engrams = load_json(ENGRAM_JSON)

    results: list[dict] = []

    item_entries = items.get("items", [])
    engram_entries = engrams.get("engrams", [])

    print(f"[ICON SYNC] Items: {len(item_entries)}")
    for index, entry in enumerate(item_entries, 1):
        result = sync_entry(entry, "items", args.force)
        results.append({"kind": "item", **result})
        print(
            f"[ICON SYNC] item {index}/{len(item_entries)} "
            f"{result['status']}: {result['name']}"
        )
        time.sleep(max(0.0, args.delay))

    print(f"[ICON SYNC] Engrams: {len(engram_entries)}")
    for index, entry in enumerate(engram_entries, 1):
        result = sync_entry(entry, "engrams", args.force)
        results.append({"kind": "engram", **result})
        print(
            f"[ICON SYNC] engram {index}/{len(engram_entries)} "
            f"{result['status']}: {result['name']}"
        )
        time.sleep(max(0.0, args.delay))

    items["iconSource"] = "ARK Fandom public file assets"
    items["runtimeNetworkRequired"] = False
    engrams["iconSource"] = "ARK Fandom public file assets"
    engrams["runtimeNetworkRequired"] = False

    save_json(ITEM_JSON, items)
    save_json(ENGRAM_JSON, engrams)

    downloaded = sum(1 for r in results if r["status"] == "downloaded")
    cached = sum(1 for r in results if r["status"] == "cached")
    missing = [r for r in results if r["status"] == "missing"]

    report = {
        "source": "https://ark.fandom.com/wiki/",
        "runtimeNetworkRequired": False,
        "downloaded": downloaded,
        "cached": cached,
        "missingCount": len(missing),
        "missing": missing,
        "results": results,
    }
    save_json(REPORT_JSON, report)

    print(
        f"[ICON SYNC] complete downloaded={downloaded} "
        f"cached={cached} missing={len(missing)}"
    )

    if missing:
        print("[ICON SYNC] unresolved icons:", file=sys.stderr)
        for result in missing:
            print(
                f"  {result['kind']}: {result['name']} "
                f"({result['className']})",
                file=sys.stderr,
            )

    return 2 if args.strict and missing else 0


if __name__ == "__main__":
    raise SystemExit(main())
