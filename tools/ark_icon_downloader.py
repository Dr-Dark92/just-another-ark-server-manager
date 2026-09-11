#!/usr/bin/env python3
"""
Standalone local ARK icon downloader for JAASM.

This tool is intentionally NOT part of CI and NOT used by JAASM at runtime.
Run it manually on a normal desktop connection, then review/upload the results.

Outputs by default:
  ark-icon-downloads/items/*
  ark-icon-downloads/engrams/*
  ark-icon-downloads/download-manifest.json
  ark-icon-downloads/filenames.txt

Usage:
  python tools/ark_icon_downloader.py
  python tools/ark_icon_downloader.py --force
  python tools/ark_icon_downloader.py --source fandom
  python tools/ark_icon_downloader.py --source official
  python tools/ark_icon_downloader.py --output D:\\ark-icons
"""

from __future__ import annotations

import argparse
import html
import json
import re
import ssl
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from html.parser import HTMLParser
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "src" / "JAASM.App" / "Data"
ITEM_JSON = DATA / "ark-items.json"
ENGRAM_JSON = DATA / "ark-engrams.json"
DEFAULT_OUTPUT = ROOT / "ark-icon-downloads"

SOURCES = {
    "fandom": "https://ark.fandom.com/wiki/",
    "official": "https://ark.wiki.gg/wiki/",
}

USER_AGENT = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
    "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/153.0 Safari/537.36"
)

IMAGE_MAGIC = (
    b"\x89PNG\r\n\x1a\n",
    b"RIFF",
    b"\xff\xd8\xff",
    b"GIF87a",
    b"GIF89a",
)


def safe_name(value: str) -> str:
    value = value.strip() or "unnamed"
    return re.sub(r'[\\/:*?"<>|]', "_", value)


def page_slug(name: str) -> str:
    return urllib.parse.quote(name.replace(" ", "_"), safe="_-'()")


def request_bytes(url: str, accept: str, timeout: int = 35) -> tuple[bytes, str, str]:
    req = urllib.request.Request(
        url,
        headers={
            "User-Agent": USER_AGENT,
            "Accept": accept,
            "Referer": urllib.parse.urljoin(url, "/"),
        },
    )
    with urllib.request.urlopen(req, timeout=timeout, context=ssl.create_default_context()) as response:
        return response.read(), response.geturl(), response.headers.get("Content-Type", "")


def is_image(data: bytes, content_type: str) -> bool:
    return (content_type or "").lower().startswith("image/") or any(data.startswith(x) for x in IMAGE_MAGIC)


def absolute_url(base: str, value: str) -> str:
    value = html.unescape(value.strip())
    if value.startswith("//"):
        return "https:" + value
    return urllib.parse.urljoin(base, value)


class WikiImageParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.images: list[dict[str, str]] = []
        self.meta_images: list[str] = []

    def handle_starttag(self, tag: str, attrs) -> None:
        a = dict(attrs)
        if tag == "img":
            src = a.get("data-src") or a.get("src") or ""
            srcset = a.get("data-srcset") or a.get("srcset") or ""
            alt = a.get("alt") or ""
            cls = a.get("class") or ""
            if src:
                self.images.append({"src": src, "srcset": srcset, "alt": alt, "class": cls})
        elif tag == "meta":
            prop = (a.get("property") or a.get("name") or "").lower()
            content = a.get("content") or ""
            if prop in ("og:image", "twitter:image") and content:
                self.meta_images.append(content)


def largest_srcset(srcset: str) -> str:
    best_url = ""
    best_width = -1
    for part in srcset.split(","):
        bits = part.strip().split()
        if not bits:
            continue
        width = 0
        if len(bits) > 1 and bits[-1].endswith("w"):
            try:
                width = int(bits[-1][:-1])
            except ValueError:
                width = 0
        if width >= best_width:
            best_url = bits[0]
            best_width = width
    return best_url


def score_image(img: dict[str, str], display_name: str) -> int:
    src = urllib.parse.unquote(img.get("src", "")).lower()
    alt = img.get("alt", "").lower()
    cls = img.get("class", "").lower()
    name = display_name.lower()
    tokens = [x for x in re.findall(r"[a-z0-9]+", name) if len(x) > 2]

    score = 0
    if name in alt:
        score += 120
    if tokens and all(t in alt for t in tokens):
        score += 80
    if tokens and all(t in src for t in tokens):
        score += 60
    if any(x in cls for x in ("pi-image", "portable-infobox", "infobox")):
        score += 90
    if any(x in src for x in ("256", "512", "icon")):
        score += 15
    if any(x in src for x in ("logo", "avatar", "favicon", "site-logo", "disambig", "community", "wordmark")):
        score -= 250
    if any(x in alt for x in ("logo", "community", "disambiguation")):
        score -= 150
    return score


def discover_icon(page_url: str, display_name: str) -> tuple[bytes, str, str, str] | None:
    body, final_page, ctype = request_bytes(page_url, "text/html,application/xhtml+xml,*/*;q=0.8")
    if "html" not in ctype.lower() and not body.lstrip().startswith(b"<"):
        return None

    parser = WikiImageParser()
    parser.feed(body.decode("utf-8", errors="replace"))

    candidates: list[tuple[int, str]] = []
    for img in parser.images:
        chosen = largest_srcset(img["srcset"]) or img["src"]
        chosen = absolute_url(final_page, chosen)
        if chosen.startswith("http"):
            candidates.append((score_image({**img, "src": chosen}, display_name), chosen))

    for meta in parser.meta_images:
        candidates.append((5, absolute_url(final_page, meta)))

    candidates.sort(key=lambda x: x[0], reverse=True)

    for score, image_url in candidates:
        if score < 0:
            continue
        try:
            data, final_image, image_type = request_bytes(
                image_url,
                "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8",
            )
        except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError, ValueError):
            continue
        if data and is_image(data, image_type):
            return data, final_image, image_type, final_page

    return None


def load_entries() -> tuple[list[dict], list[dict]]:
    items_doc = json.loads(ITEM_JSON.read_text(encoding="utf-8"))
    engrams_doc = json.loads(ENGRAM_JSON.read_text(encoding="utf-8"))
    return items_doc.get("items", []), engrams_doc.get("engrams", [])


def source_order(selected: str) -> list[str]:
    if selected == "auto":
        return ["fandom", "official"]
    return [selected]


def download_entry(entry: dict, kind: str, out_dir: Path, selected_source: str, force: bool) -> dict:
    name = str(entry.get("displayName", "")).strip()
    class_name = str(entry.get("className", "")).strip()
    target = out_dir / kind / f"{safe_name(name)}.png"
    target.parent.mkdir(parents=True, exist_ok=True)

    if target.exists() and target.stat().st_size > 64 and not force:
        return {
            "kind": kind,
            "name": name,
            "className": class_name,
            "status": "cached",
            "file": str(target.relative_to(out_dir)).replace("\\", "/"),
        }

    attempts: list[dict] = []
    for source_name in source_order(selected_source):
        base = SOURCES[source_name]
        page_url = base + page_slug(name)
        try:
            resolved = discover_icon(page_url, name)
        except urllib.error.HTTPError as ex:
            attempts.append({"source": source_name, "page": page_url, "error": f"HTTP {ex.code}"})
            continue
        except (urllib.error.URLError, TimeoutError, ValueError) as ex:
            attempts.append({"source": source_name, "page": page_url, "error": str(ex)})
            continue

        if not resolved:
            attempts.append({"source": source_name, "page": page_url, "error": "no usable image found"})
            continue

        data, final_image, image_type, final_page = resolved
        target.write_bytes(data)
        return {
            "kind": kind,
            "name": name,
            "className": class_name,
            "status": "downloaded",
            "source": source_name,
            "pageUrl": final_page,
            "imageUrl": final_image,
            "contentType": image_type,
            "bytes": len(data),
            "file": str(target.relative_to(out_dir)).replace("\\", "/"),
            "attempts": attempts,
        }

    return {
        "kind": kind,
        "name": name,
        "className": class_name,
        "status": "missing",
        "file": str(target.relative_to(out_dir)).replace("\\", "/"),
        "attempts": attempts,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Download ARK catalogue icons locally for JAASM")
    parser.add_argument("--output", default=str(DEFAULT_OUTPUT), help="Download directory")
    parser.add_argument("--source", choices=["auto", "fandom", "official"], default="auto")
    parser.add_argument("--force", action="store_true", help="Overwrite existing icon files")
    parser.add_argument("--delay", type=float, default=0.15, help="Delay between entries")
    args = parser.parse_args()

    out_dir = Path(args.output).expanduser().resolve()
    out_dir.mkdir(parents=True, exist_ok=True)

    items, engrams = load_entries()
    jobs = [("items", x) for x in items] + [("engrams", x) for x in engrams]
    results: list[dict] = []

    print(f"[ARK ICON DOWNLOADER] Output: {out_dir}")
    print(f"[ARK ICON DOWNLOADER] Source mode: {args.source}")
    print(f"[ARK ICON DOWNLOADER] Entries: {len(jobs)}")

    for index, (kind, entry) in enumerate(jobs, 1):
        result = download_entry(entry, kind, out_dir, args.source, args.force)
        results.append(result)
        print(f"[{index:03}/{len(jobs):03}] {result['status']:<10} {kind:<7} {result['name']}")
        time.sleep(max(0.0, args.delay))

    downloaded = sum(r["status"] == "downloaded" for r in results)
    cached = sum(r["status"] == "cached" for r in results)
    missing = sum(r["status"] == "missing" for r in results)

    manifest = {
        "sourceMode": args.source,
        "runtimeNetworkRequired": False,
        "downloaded": downloaded,
        "cached": cached,
        "missing": missing,
        "total": len(results),
        "results": results,
    }
    (out_dir / "download-manifest.json").write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )

    filenames = [r["file"] for r in results if r["status"] in ("downloaded", "cached")]
    (out_dir / "filenames.txt").write_text("\n".join(filenames) + ("\n" if filenames else ""), encoding="utf-8")

    print()
    print(f"[ARK ICON DOWNLOADER] complete downloaded={downloaded} cached={cached} missing={missing}")
    print(f"[ARK ICON DOWNLOADER] manifest: {out_dir / 'download-manifest.json'}")
    print(f"[ARK ICON DOWNLOADER] names:    {out_dir / 'filenames.txt'}")

    return 0 if downloaded + cached > 0 else 3


if __name__ == "__main__":
    raise SystemExit(main())
