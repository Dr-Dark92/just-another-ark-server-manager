#!/usr/bin/env python3
"""
Build-time ARK icon synchronizer for JAASM.

Primary source is ARK Fandom, as requested for the fan-content bundle. GitHub
hosted runners are currently blocked from resolving Fandom pages, so this
resolver also falls back to the ARK Official Community Wiki (ark.wiki.gg),
which carries the same public ARK item/engram artwork and is accessible from CI.

JAASM itself never contacts either wiki at runtime. The build downloads the
artwork once and packages it under Data/Icons.
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

WIKIS = (
    ("fandom", "https://ark.fandom.com/wiki/"),
    ("official", "https://ark.wiki.gg/wiki/"),
)

USER_AGENT = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
    "AppleWebKit/537.36 (KHTML, like Gecko) "
    "Chrome/153.0.0.0 Safari/537.36"
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


def page_slug(name: str) -> str:
    return urllib.parse.quote(name.replace(" ", "_"), safe="_-'()")


def request_bytes(
    url: str,
    accept: str,
    referer: str | None = None,
    timeout: int = 35,
) -> tuple[bytes, str, str]:
    headers = {
        "User-Agent": USER_AGENT,
        "Accept": accept,
        "Accept-Language": "en-US,en;q=0.9",
    }
    if referer:
        headers["Referer"] = referer

    request = urllib.request.Request(url, headers=headers)
    context = ssl.create_default_context()

    with urllib.request.urlopen(request, timeout=timeout, context=context) as response:
        return (
            response.read(),
            response.geturl(),
            response.headers.get("Content-Type", ""),
        )


def is_image(data: bytes, content_type: str) -> bool:
    ctype = (content_type or "").lower()
    return ctype.startswith("image/") or any(data.startswith(magic) for magic in IMAGE_MAGIC)


class ImageParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.images: list[dict[str, str]] = []
        self.meta: list[str] = []

    def handle_starttag(self, tag: str, attrs):
        values = dict(attrs)

        if tag == "img":
            src = values.get("data-src") or values.get("src") or ""
            srcset = values.get("data-srcset") or values.get("srcset") or ""
            alt = values.get("alt") or ""
            css = values.get("class") or ""
            width = values.get("width") or ""
            height = values.get("height") or ""

            if src:
                self.images.append(
                    {
                        "src": src,
                        "srcset": srcset,
                        "alt": alt,
                        "class": css,
                        "width": width,
                        "height": height,
                    }
                )

        elif tag == "meta":
            prop = (values.get("property") or values.get("name") or "").lower()
            content = values.get("content") or ""
            if prop in ("og:image", "twitter:image") and content:
                self.meta.append(content)


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


def normalize_image_url(raw: str, page_url: str) -> str:
    raw = html.unescape(raw.strip())
    if raw.startswith("//"):
        return "https:" + raw
    return urllib.parse.urljoin(page_url, raw)


def score_image(candidate: dict[str, str], display_name: str) -> int:
    src = urllib.parse.unquote(candidate["src"]).lower()
    alt = candidate["alt"].lower()
    css = candidate["class"].lower()
    name = display_name.lower()
    tokens = [t for t in re.findall(r"[a-z0-9]+", name) if len(t) > 2]

    score = 0

    if name and name in alt:
        score += 150
    if tokens and all(t in alt for t in tokens):
        score += 100
    if tokens and all(t in src for t in tokens):
        score += 75

    # Fandom and wiki.gg infobox artwork.
    if any(marker in css for marker in ("pi-image", "infobox", "mw-file-element")):
        score += 60

    # ARK wiki catalogue icons are normally 256x256.
    try:
        width = int(candidate.get("width") or 0)
        height = int(candidate.get("height") or 0)
        if width == height and width >= 128:
            score += 25
    except ValueError:
        pass

    if any(size in src for size in ("256", "512")):
        score += 15

    bad = (
        "logo",
        "avatar",
        "favicon",
        "site-logo",
        "disambig",
        "dlc_icon",
        "community",
        "wordmark",
        "button",
    )
    if any(value in src for value in bad):
        score -= 300
    if any(value in alt for value in bad):
        score -= 200

    return score


def discover_icon(page_url: str, display_name: str) -> tuple[str, str] | None:
    try:
        body, final_page, content_type = request_bytes(
            page_url,
            "text/html,application/xhtml+xml,*/*;q=0.8",
            referer=page_url,
        )
    except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError, ValueError):
        return None

    if "html" not in content_type.lower() and not body.lstrip().startswith(b"<"):
        return None

    parser = ImageParser()
    parser.feed(body.decode("utf-8", errors="replace"))

    candidates: list[tuple[int, str]] = []

    for image in parser.images:
        raw = largest_srcset(image["srcset"]) or image["src"]
        url = normalize_image_url(raw, final_page)
        if not url.startswith("http"):
            continue

        score = score_image({**image, "src": url}, display_name)
        candidates.append((score, url))

    # Meta images are only a last-resort option because they may be wiki branding.
    for meta in parser.meta:
        candidates.append((1, normalize_image_url(meta, final_page)))

    # Avoid retrying duplicate thumbnail URLs.
    ordered: list[tuple[int, str]] = []
    seen: set[str] = set()
    for score, url in sorted(candidates, key=lambda value: value[0], reverse=True):
        # Strip common MediaWiki thumbnail suffixes so we keep the best-resolution asset.
        canonical = re.sub(r"/revision/latest/.*$", "/revision/latest", url)
        if canonical in seen:
            continue
        seen.add(canonical)
        ordered.append((score, url))

    for score, url in ordered[:24]:
        if score < 0:
            continue

        try:
            data, final_url, image_type = request_bytes(
                url,
                "image/avif,image/webp,image/apng,image/png,image/jpeg,image/*,*/*;q=0.8",
                referer=final_page,
            )
            if data and is_image(data, image_type):
                return final_url, final_page
        except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError, ValueError):
            continue

    return None


def download_icon(icon_url: str, page_url: str) -> tuple[bytes, str, str] | None:
    try:
        data, final_url, content_type = request_bytes(
            icon_url,
            "image/avif,image/webp,image/apng,image/png,image/jpeg,image/*,*/*;q=0.8",
            referer=page_url,
        )
        if data and is_image(data, content_type):
            return data, final_url, content_type
    except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError, ValueError):
        pass

    return None


def page_candidates(entry: dict) -> list[tuple[str, str]]:
    name = str(entry.get("displayName", "")).strip()
    category = str(entry.get("category", "")).strip()
    source = str(entry.get("sourceUrl", "")).strip()

    candidates: list[tuple[str, str]] = []

    # Preserve any specific previously known source page first.
    if (
        source
        and "/wiki/" in source
        and not any(term in source for term in ("Item_IDs", "Engram_class_names"))
    ):
        candidates.append(("catalog", source))

    for label, base in WIKIS:
        candidates.append((label, base + page_slug(name)))

        if category and category not in ("Resources", "Consumables", "Base Game", "ASA"):
            candidates.append((label, base + page_slug(f"{name} ({category})")))

    deduped: list[tuple[str, str]] = []
    seen: set[str] = set()
    for label, url in candidates:
        if url in seen:
            continue
        seen.add(url)
        deduped.append((label, url))

    return deduped


def sync_entry(entry: dict, folder: str, force: bool) -> dict:
    name = str(entry.get("displayName", "")).strip()
    class_name = str(entry.get("className", "")).strip()
    icon_file = str(entry.get("iconFile", "")).strip()

    if not icon_file:
        icon_file = f"Icons/{folder}/{safe_name(name)}.png"
        entry["iconFile"] = icon_file

    target = DATA / icon_file

    if target.exists() and target.stat().st_size > 64 and not force:
        return {
            "name": name,
            "className": class_name,
            "status": "cached",
            "iconFile": icon_file,
        }

    target.parent.mkdir(parents=True, exist_ok=True)
    tried: list[dict[str, str]] = []

    for source_name, page_url in page_candidates(entry):
        tried.append({"source": source_name, "page": page_url})

        resolved = discover_icon(page_url, name)
        if not resolved:
            continue

        icon_url, final_page = resolved
        fetched = download_icon(icon_url, final_page)
        if not fetched:
            continue

        data, final_icon, content_type = fetched
        target.write_bytes(data)
        entry["sourceUrl"] = final_page

        return {
            "name": name,
            "className": class_name,
            "status": "downloaded",
            "sourceProvider": source_name,
            "iconFile": icon_file,
            "bytes": len(data),
            "contentType": content_type,
            "pageUrl": final_page,
            "sourceUrl": final_icon,
        }

    return {
        "name": name,
        "className": class_name,
        "status": "missing",
        "iconFile": icon_file,
        "pagesTried": tried,
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
    parser.add_argument("--force", action="store_true")
    parser.add_argument("--strict", action="store_true")
    parser.add_argument("--delay", type=float, default=0.08)
    args = parser.parse_args()

    items = load_json(ITEM_JSON)
    engrams = load_json(ENGRAM_JSON)

    groups = (
        ("item", "items", items.get("items", [])),
        ("engram", "engrams", engrams.get("engrams", [])),
    )

    results: list[dict] = []

    for label, folder, entries in groups:
        print(f"[ICON SYNC] {label}s: {len(entries)}")
        for index, entry in enumerate(entries, 1):
            result = sync_entry(entry, folder, args.force)
            results.append({"kind": label, **result})
            print(
                f"[ICON SYNC] {label} {index}/{len(entries)} "
                f"{result['status']}: {result['name']}"
            )
            time.sleep(max(0.0, args.delay))

    for document in (items, engrams):
        document["iconSource"] = "ARK Fandom with ARK Official Community Wiki fallback"
        document["runtimeNetworkRequired"] = False

    save_json(ITEM_JSON, items)
    save_json(ENGRAM_JSON, engrams)

    downloaded = sum(result["status"] == "downloaded" for result in results)
    cached = sum(result["status"] == "cached" for result in results)
    missing = [result for result in results if result["status"] == "missing"]
    fandom_downloaded = sum(
        result.get("sourceProvider") == "fandom" and result["status"] == "downloaded"
        for result in results
    )
    official_downloaded = sum(
        result.get("sourceProvider") == "official" and result["status"] == "downloaded"
        for result in results
    )

    report = {
        "sources": [base for _, base in WIKIS],
        "resolver": "page-html-image-discovery-v3",
        "runtimeNetworkRequired": False,
        "downloaded": downloaded,
        "cached": cached,
        "fandomDownloaded": fandom_downloaded,
        "officialWikiDownloaded": official_downloaded,
        "missingCount": len(missing),
        "missing": missing,
        "results": results,
    }
    save_json(REPORT_JSON, report)

    print(
        f"[ICON SYNC] complete downloaded={downloaded} cached={cached} "
        f"missing={len(missing)} fandom={fandom_downloaded} "
        f"official={official_downloaded}"
    )

    if downloaded + cached == 0:
        print(
            "[ICON SYNC] FATAL: zero icons resolved from either wiki source.",
            file=sys.stderr,
        )
        return 3

    return 2 if args.strict and missing else 0


if __name__ == "__main__":
    raise SystemExit(main())
