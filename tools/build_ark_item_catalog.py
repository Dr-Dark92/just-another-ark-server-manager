#!/usr/bin/env python3
"""
Build JAASM's local ARK item catalogue from the ARK Fandom Item IDs page.

Primary source snapshot:
  a locally saved copy of https://ark.fandom.com/wiki/Item_IDs

This builder is intentionally OFFLINE-ONLY. It never calls Fandom, MediaWiki,
or any remote API. Save/export the source HTML first, then run the parser. It writes:
  src/JAASM.App/Data/ark-items.json
  src/JAASM.App/Data/Icons/items/*

Runtime JAASM never depends on Fandom; this is a developer/update tool only.
"""

from __future__ import annotations

import argparse
import html
import json
import os
import re
import sys
import time
import urllib.parse
import urllib.request
from html.parser import HTMLParser
from pathlib import Path

SOURCE_PAGE = "https://ark.fandom.com/wiki/Item_IDs"
API_URL = (
    "https://ark.fandom.com/api.php?"
    "action=parse&page=Item_IDs&prop=text&format=json&formatversion=2"
)

CLASS_RE = re.compile(r"\b(PrimalItem[A-Za-z0-9_]+_C)\b")
BLUEPRINT_CLASS_RE = re.compile(r"\.([A-Za-z0-9_]+_C)[\"']?\)?$")
INTEGER_RE = re.compile(r"^\d+$")


class TableParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.rows: list[dict] = []
        self._in_tr = False
        self._in_cell = False
        self._cell_text: list[str] = []
        self._cells: list[str] = []
        self._images: list[str] = []
        self._links: list[str] = []

    def handle_starttag(self, tag: str, attrs):
        attr = dict(attrs)
        if tag == "tr":
            self._in_tr = True
            self._cells = []
            self._images = []
            self._links = []
        elif self._in_tr and tag in ("td", "th"):
            self._in_cell = True
            self._cell_text = []
        elif self._in_tr and tag == "img":
            src = attr.get("data-src") or attr.get("src")
            if src:
                self._images.append(src)
        elif self._in_tr and tag == "a":
            href = attr.get("href")
            if href:
                self._links.append(href)

    def handle_endtag(self, tag: str):
        if self._in_tr and tag in ("td", "th") and self._in_cell:
            value = " ".join("".join(self._cell_text).split())
            self._cells.append(value)
            self._in_cell = False
        elif tag == "tr" and self._in_tr:
            if self._cells:
                self.rows.append(
                    {
                        "cells": self._cells[:],
                        "images": self._images[:],
                        "links": self._links[:],
                    }
                )
            self._in_tr = False

    def handle_data(self, data: str):
        if self._in_cell:
            self._cell_text.append(data)


def load_source_html(local_html: str | None) -> str:
    if not local_html:
        raise RuntimeError(
            "Offline catalogue build requires --html <saved Item_IDs page>. "
            "JAASM intentionally does not fetch wiki/API data automatically."
        )

    return Path(local_html).read_text(encoding="utf-8", errors="replace")


def normalize_url(value: str) -> str:
    value = html.unescape(value.strip())
    if value.startswith("//"):
        return "https:" + value
    if value.startswith("/"):
        return urllib.parse.urljoin("https://ark.fandom.com", value)
    return value


def clean_icon_url(value: str) -> str:
    value = normalize_url(value)
    # Fandom thumbnails often append /revision/latest/scale-to-width-down/<n>.
    # Keep the URL as served; the downloader follows redirects.
    return value


def extract_class_name(cells: list[str]) -> str:
    joined = " | ".join(cells)
    match = CLASS_RE.search(joined)
    if match:
        return match.group(1)

    # Some tables expose a full Blueprint path rather than just the generated class.
    for cell in cells:
        m = BLUEPRINT_CLASS_RE.search(cell.strip())
        if m and m.group(1).startswith("PrimalItem"):
            return m.group(1)

    return ""


def extract_item_id(cells: list[str]) -> str:
    # Numeric IDs are generally short integer cells. Preserve as text to avoid
    # implying that all ARK items have a stable numeric ID.
    for cell in cells[1:]:
        candidate = cell.strip()
        if INTEGER_RE.match(candidate) and len(candidate) <= 6:
            return candidate
    return ""


def choose_name(cells: list[str], class_name: str) -> str:
    for value in cells:
        v = value.strip()
        if not v:
            continue
        if v == class_name:
            continue
        if "PrimalItem" in v or "Blueprint" in v:
            continue
        if INTEGER_RE.match(v):
            continue
        if len(v) > 100:
            continue
        return v
    return class_name or "Unknown Item"


def choose_icon(images: list[str]) -> str:
    for image in images:
        url = clean_icon_url(image)
        if url and not any(x in url.lower() for x in ("pixel.gif", "site-logo", "favicon")):
            return url
    return ""


def choose_source_url(links: list[str]) -> str:
    for link in links:
        url = normalize_url(link)
        if "/wiki/" in url and "Item_IDs" not in url:
            return url
    return SOURCE_PAGE


def classify(name: str, class_name: str) -> tuple[str, str, bool, list[str]]:
    lower = name.lower()
    klass = class_name.lower()
    aliases: set[str] = set()

    if "berry" in lower or "berry" in klass:
        category, sub = "Consumables", "Berries"
        aliases.update(("berry", "berries"))
        harvest = True
    elif any(word in lower for word in ("meat", "mutton", "lamb")) or "meat" in klass.lower():
        category, sub = "Consumables", "Meat"
        aliases.update(("meat",))
        if "fish" in lower:
            aliases.add("fish")
            sub = "Fish"
        harvest = "cooked" not in lower
    elif class_name.startswith("PrimalItemResource_"):
        category, sub, harvest = "Resources", "Resources", True
    elif class_name.startswith("PrimalItemConsumable_"):
        category, sub, harvest = "Consumables", "Other", False
    elif "ammo" in klass:
        category, sub, harvest = "Ammo", "Ammo", False
    elif "weapon" in klass:
        category, sub, harvest = "Weapons", "Weapons", False
    elif "armor" in klass:
        category, sub, harvest = "Armor", "Armor", False
    else:
        category, sub, harvest = "Items", "Other", False

    for token in re.findall(r"[A-Za-z0-9]+", name.lower()):
        if len(token) >= 3:
            aliases.add(token)

    return category, sub, harvest, sorted(aliases)


def icon_extension(url: str) -> str:
    path = urllib.parse.urlparse(url).path.lower()
    for ext in (".webp", ".png", ".jpg", ".jpeg", ".gif"):
        if ext in path:
            return ext
    return ".png"


def slugify(name: str, class_name: str) -> str:
    seed = name or class_name
    slug = re.sub(r"[^a-z0-9]+", "-", seed.lower()).strip("-")
    return slug[:80] or "item"


def download_icon(url: str, target: Path) -> bool:
    if not url:
        return False
    try:
        req = urllib.request.Request(
            url,
            headers={"User-Agent": "Mozilla/5.0 JAASM-CatalogBuilder/1.0"},
        )
        with urllib.request.urlopen(req, timeout=45) as response:
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(response.read())
        return target.stat().st_size > 0
    except Exception as exc:
        print(f"[WARN] icon failed: {url}: {exc}", file=sys.stderr)
        return False


def build(html_text: str, icon_dir: Path, download_icons: bool) -> list[dict]:
    parser = TableParser()
    parser.feed(html_text)

    output: list[dict] = []
    seen: set[str] = set()

    for row in parser.rows:
        cells = row["cells"]
        class_name = extract_class_name(cells)
        if not class_name or class_name in seen:
            continue

        seen.add(class_name)
        name = choose_name(cells, class_name)
        item_id = extract_item_id(cells)
        icon_url = choose_icon(row["images"])
        source_url = choose_source_url(row["links"])
        category, subcategory, harvest_eligible, aliases = classify(name, class_name)

        icon_file = ""
        if icon_url and download_icons:
            filename = slugify(name, class_name) + icon_extension(icon_url)
            target = icon_dir / filename
            if download_icon(icon_url, target):
                icon_file = f"Icons/items/{filename}"
            time.sleep(0.03)

        output.append(
            {
                "displayName": name,
                "className": class_name,
                "category": category,
                "subCategory": subcategory,
                "itemId": item_id,
                "aliases": aliases,
                "iconFile": icon_file,
                "sourceUrl": source_url,
                "harvestEligible": harvest_eligible,
            }
        )

    output.sort(key=lambda x: (x["category"], x["displayName"].lower()))
    return output


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--html", required=True, help="Locally saved Fandom Item_IDs HTML file")
    parser.add_argument(
        "--output",
        default="src/JAASM.App/Data/ark-items.json",
        help="Output JSON catalogue path",
    )
    parser.add_argument(
        "--icon-dir",
        default="src/JAASM.App/Data/Icons/items",
        help="Downloaded icon directory",
    )
    parser.add_argument(
        "--no-icons",
        action="store_true",
        help="Build names/classes only; do not download icons",
    )
    args = parser.parse_args()

    html_text = load_source_html(args.html)
    output_path = Path(args.output)
    icon_dir = Path(args.icon_dir)

    items = build(html_text, icon_dir, not args.no_icons)
    if not items:
        raise RuntimeError(
            "No PrimalItem classes were found. The source layout may have changed."
        )

    payload = {
        "source": SOURCE_PAGE,
        "sourceMode": "offline-snapshot",
        "runtimeNetworkRequired": False,
        "generatedBy": "tools/build_ark_item_catalog.py",
        "itemCount": len(items),
        "items": items,
    }

    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(
        json.dumps(payload, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )

    harvest_count = sum(1 for item in items if item["harvestEligible"])
    icon_count = sum(1 for item in items if item["iconFile"])
    print(
        f"Wrote {len(items)} items to {output_path} "
        f"({harvest_count} harvest eligible, {icon_count} icons)."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
