#!/usr/bin/env python3
"""
Build-time ARK icon synchronizer for JAASM.

The previous implementation guessed Fandom file names. That is unreliable.
This version resolves the public wiki PAGE for each catalogue entry, parses the
page HTML for image candidates, scores those candidates, downloads the best
actual image, and stores it locally. JAASM itself remains fully offline.

Outputs:
  src/JAASM.App/Data/Icons/items/*.png
  src/JAASM.App/Data/Icons/engrams/*.png
  src/JAASM.App/Data/icon-sync-report.json
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
REPORT_JSON = DATA / "icon-sync-report.json"
WIKI = "https://ark.fandom.com/wiki/"
USER_AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/153.0 Safari/537.36"
IMAGE_MAGIC = (b"\x89PNG\r\n\x1a\n", b"RIFF", b"\xff\xd8\xff", b"GIF87a", b"GIF89a")


def safe_name(value: str) -> str:
    return re.sub(r'[\\/:*?"<>|]', "_", value).strip()


def page_slug(name: str) -> str:
    return urllib.parse.quote(name.replace(" ", "_"), safe="_-'()")


def request_bytes(url: str, accept: str, timeout: int = 35) -> tuple[bytes, str, str]:
    req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT, "Accept": accept, "Referer": WIKI})
    with urllib.request.urlopen(req, timeout=timeout, context=ssl.create_default_context()) as response:
        return response.read(), response.geturl(), response.headers.get("Content-Type", "")


def is_image(data: bytes, content_type: str) -> bool:
    return (content_type or "").lower().startswith("image/") or any(data.startswith(x) for x in IMAGE_MAGIC)


class ImageParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.images: list[dict[str, str]] = []
        self.meta: list[str] = []

    def handle_starttag(self, tag: str, attrs):
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
                self.meta.append(content)


def absolute_url(url: str) -> str:
    url = html.unescape(url.strip())
    if url.startswith("//"):
        return "https:" + url
    if url.startswith("/"):
        return urllib.parse.urljoin("https://ark.fandom.com", url)
    return url


def largest_srcset(srcset: str) -> str:
    best = ""
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
                pass
        if width >= best_width:
            best, best_width = bits[0], width
    return best


def score_image(candidate: dict[str, str], display_name: str) -> int:
    src = candidate["src"].lower()
    alt = candidate["alt"].lower()
    cls = candidate["class"].lower()
    name = display_name.lower()
    tokens = [x for x in re.findall(r"[a-z0-9]+", name) if len(x) > 2]
    score = 0
    if name in alt: score += 100
    if all(t in alt for t in tokens): score += 70
    if all(t in urllib.parse.unquote(src) for t in tokens): score += 45
    if "pi-image" in cls or "portable-infobox" in cls: score += 80
    if "256" in src or "512" in src: score += 10
    if any(x in src for x in ("logo", "avatar", "favicon", "site-logo", "disambig", "dlc_icon")): score -= 200
    if any(x in alt for x in ("logo", "disambig", "community")): score -= 100
    return score


def discover_icon(page_url: str, display_name: str) -> tuple[str, str] | None:
    try:
        body, final_page, ctype = request_bytes(page_url, "text/html,application/xhtml+xml,*/*;q=0.8")
    except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError, ValueError):
        return None
    if "html" not in ctype.lower() and not body.lstrip().startswith(b"<"):
        return None
    parser = ImageParser()
    parser.feed(body.decode("utf-8", errors="replace"))
    candidates: list[tuple[int, str]] = []
    for img in parser.images:
        chosen = largest_srcset(img["srcset"]) or img["src"]
        chosen = absolute_url(chosen)
        if chosen.startswith("http"):
            candidates.append((score_image({**img, "src": chosen}, display_name), chosen))
    for meta in parser.meta:
        candidates.append((5, absolute_url(meta)))
    candidates.sort(key=lambda x: x[0], reverse=True)
    for score, url in candidates:
        if score < 0:
            continue
        try:
            data, final_url, image_type = request_bytes(url, "image/avif,image/webp,image/apng,image/*,*/*;q=0.8")
            if data and is_image(data, image_type):
                return final_url, final_page
        except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError, ValueError):
            continue
    return None


def download_icon(url: str) -> tuple[bytes, str, str] | None:
    try:
        data, final_url, ctype = request_bytes(url, "image/avif,image/webp,image/apng,image/*,*/*;q=0.8")
        return (data, final_url, ctype) if data and is_image(data, ctype) else None
    except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError, ValueError):
        return None


def page_candidates(entry: dict) -> list[str]:
    name = str(entry.get("displayName", "")).strip()
    urls: list[str] = []
    source = str(entry.get("sourceUrl", "")).strip()
    # Only use sourceUrl when it is already an item-specific wiki page.
    if source and "/wiki/" in source and not any(x in source for x in ("Item_IDs", "Engram_class_names")):
        urls.append(source)
    urls.append(WIKI + page_slug(name))
    # Common Fandom DLC naming convention; useful for resources such as Raw Salt.
    category = str(entry.get("category", ""))
    if category and category not in ("Resources", "Consumables", "Base Game", "ASA"):
        urls.append(WIKI + page_slug(f"{name} ({category})"))
    return list(dict.fromkeys(urls))


def sync_entry(entry: dict, kind: str, force: bool) -> dict:
    name = str(entry.get("displayName", "")).strip()
    class_name = str(entry.get("className", "")).strip()
    icon_file = str(entry.get("iconFile", "")).strip() or f"Icons/{kind}/{safe_name(name)}.png"
    entry["iconFile"] = icon_file
    target = DATA / icon_file
    if target.exists() and target.stat().st_size > 64 and not force:
        return {"name": name, "className": class_name, "status": "cached", "iconFile": icon_file}
    target.parent.mkdir(parents=True, exist_ok=True)
    tried: list[str] = []
    for page_url in page_candidates(entry):
        tried.append(page_url)
        resolved = discover_icon(page_url, name)
        if not resolved:
            continue
        icon_url, final_page = resolved
        fetched = download_icon(icon_url)
        if not fetched:
            continue
        data, final_icon, ctype = fetched
        target.write_bytes(data)
        entry["sourceUrl"] = final_page
        return {"name": name, "className": class_name, "status": "downloaded", "iconFile": icon_file,
                "bytes": len(data), "contentType": ctype, "pageUrl": final_page, "sourceUrl": final_icon}
    return {"name": name, "className": class_name, "status": "missing", "iconFile": icon_file, "pagesTried": tried}


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def save_json(path: Path, payload: dict) -> None:
    path.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--force", action="store_true")
    p.add_argument("--strict", action="store_true")
    p.add_argument("--delay", type=float, default=0.12)
    args = p.parse_args()
    items, engrams = load_json(ITEM_JSON), load_json(ENGRAM_JSON)
    groups = (("item", "items", items.get("items", [])), ("engram", "engrams", engrams.get("engrams", [])))
    results: list[dict] = []
    for label, folder, entries in groups:
        print(f"[ICON SYNC] {label}s: {len(entries)}")
        for i, entry in enumerate(entries, 1):
            result = sync_entry(entry, folder, args.force)
            results.append({"kind": label, **result})
            print(f"[ICON SYNC] {label} {i}/{len(entries)} {result['status']}: {result['name']}")
            time.sleep(max(0.0, args.delay))
    for doc in (items, engrams):
        doc["iconSource"] = "ARK Fandom public wiki pages"
        doc["runtimeNetworkRequired"] = False
    save_json(ITEM_JSON, items)
    save_json(ENGRAM_JSON, engrams)
    downloaded = sum(r["status"] == "downloaded" for r in results)
    cached = sum(r["status"] == "cached" for r in results)
    missing = [r for r in results if r["status"] == "missing"]
    report = {"source": WIKI, "resolver": "page-html-image-discovery-v2", "runtimeNetworkRequired": False,
              "downloaded": downloaded, "cached": cached, "missingCount": len(missing), "missing": missing, "results": results}
    save_json(REPORT_JSON, report)
    print(f"[ICON SYNC] complete downloaded={downloaded} cached={cached} missing={len(missing)}")
    # Zero resolved assets means the resolver itself is broken/blocked: always fail the build.
    if downloaded + cached == 0:
        print("[ICON SYNC] FATAL: zero icons resolved; refusing to publish a falsely successful asset bundle.", file=sys.stderr)
        return 3
    return 2 if args.strict and missing else 0


if __name__ == "__main__":
    raise SystemExit(main())
