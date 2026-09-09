(() => {
  const LINK_MARK = "data-jaasm-link";
  const CARD_MARK = "data-jaasm-card";
  const PAGE_MARK = "data-jaasm-page-button";

  function normalizeUrl(url) {
    try {
      const u = new URL(url, location.href);
      u.hash = "";
      return u.toString().replace(/\/$/, "");
    } catch {
      return String(url || "").replace(/\/$/, "");
    }
  }

  function extractProjectIdFromText(text) {
    const match = String(text || "").match(/Project\s*ID\s*:?\s*(\d{4,10})/i);
    return match ? match[1] : null;
  }

  function extractModIdFromText(text) {
    const match = String(text || "").match(/Mod\s*ID\s*:?\s*(\d{4,10})/i);
    return match ? match[1] : null;
  }

  function extractNumericIdFromUrl(url) {
    const arkCodes = String(url || "").match(/arkcodes\.com\/mods\/(\d{4,10})(?:\/|$)/i);
    if (arkCodes) return arkCodes[1];

    const queryId = String(url || "").match(/[?&](?:projectId|modId|id)=(\d{4,10})(?:&|$)/i);
    return queryId ? queryId[1] : null;
  }

  async function fetchOneLevel(url) {
    try {
      const response = await fetch(url, {
        credentials: "include",
        cache: "no-store",
        redirect: "follow"
      });

      if (!response.ok) return null;
      return await response.text();
    } catch {
      return null;
    }
  }

  async function resolveCurseForgeId(url, nearbyText = "") {
    const nearby = extractProjectIdFromText(nearbyText);
    if (nearby) return nearby;

    if (normalizeUrl(url) === normalizeUrl(location.href)) {
      const pageId = extractProjectIdFromText(document.body?.innerText);
      if (pageId) return pageId;
    }

    // Crawl exactly one level into the mod detail page.
    const html = await fetchOneLevel(url);
    if (!html) return null;

    const labelMatch = extractProjectIdFromText(html);
    if (labelMatch) return labelMatch;

    const jsonMatch =
      html.match(/["']projectId["']\s*:\s*["']?(\d{4,10})["']?/i) ||
      html.match(/["']id["']\s*:\s*["']?(\d{4,10})["']?/i);

    return jsonMatch ? jsonMatch[1] : null;
  }

  async function resolveArkCodesId(url, nearbyText = "") {
    const nearby = extractModIdFromText(nearbyText);
    if (nearby) return nearby;

    const numeric = extractNumericIdFromUrl(url);
    if (numeric) return numeric;

    if (normalizeUrl(url) === normalizeUrl(location.href)) {
      const pageId = extractModIdFromText(document.body?.innerText);
      if (pageId) return pageId;
    }

    // Crawl exactly one level into the ArkCodes mod page.
    const html = await fetchOneLevel(url);
    if (!html) return null;

    const labelMatch = extractModIdFromText(html);
    if (labelMatch) return labelMatch;

    const urlMatch = extractNumericIdFromUrl(url);
    return urlMatch || null;
  }

  async function resolveModId(link, host) {
    const url = link?.href || location.href;
    const nearbyText = host?.innerText || link?.parentElement?.innerText || "";

    if (/curseforge\.com$/i.test(new URL(url, location.href).hostname)) {
      return await resolveCurseForgeId(url, nearbyText);
    }

    if (/arkcodes\.com$/i.test(new URL(url, location.href).hostname)) {
      return await resolveArkCodesId(url, nearbyText);
    }

    return null;
  }

  function sendToJaasm(modId, button) {
    button.dataset.state = "working";
    button.textContent = "Adding…";
    button.disabled = true;

    chrome.runtime.sendMessage(
      { type: "JAASM_ADD_MOD", modId },
      (response) => {
        if (chrome.runtime.lastError) {
          button.dataset.state = "error";
          button.textContent = "JAASM offline";
          button.title = chrome.runtime.lastError.message;
          button.disabled = false;
          return;
        }

        if (response?.ok) {
          button.dataset.state = "success";
          button.textContent = "✓ Added";
          button.title = response.message || "Added to JAASM.";
          return;
        }

        button.dataset.state = "error";
        button.textContent =
          response?.status === 409 ? "Already added" : "Add failed";
        button.title = response?.message || "JAASM could not add this mod.";
        button.disabled = false;
      }
    );
  }

  function createButton(link, host) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "jaasm-add-mod-button";
    button.textContent = "+ JAASM";
    button.title = "Add this mod to the active JAASM server profile";

    button.addEventListener("click", async (event) => {
      event.preventDefault();
      event.stopPropagation();

      button.dataset.state = "working";
      button.textContent = "Finding ID…";
      button.disabled = true;

      const modId = await resolveModId(link, host);

      if (!modId) {
        button.dataset.state = "error";
        button.textContent = "ID not found";
        button.title =
          "JAASM crawled one level into this mod page but could not find Project ID / Mod ID.";
        button.disabled = false;
        return;
      }

      sendToJaasm(modId, button);
    });

    return button;
  }

  function isCurseForgeModLink(link) {
    try {
      const u = new URL(link.href, location.href);
      return /curseforge\.com$/i.test(u.hostname) &&
        /^\/ark-survival-ascended\/mods\/[a-z0-9-]+\/?$/i.test(u.pathname);
    } catch {
      return false;
    }
  }

  function isArkCodesModLink(link) {
    try {
      const u = new URL(link.href, location.href);
      return /arkcodes\.com$/i.test(u.hostname) &&
        /^\/mods\/(?:\d{4,10}|[a-z0-9-]+)\/?$/i.test(u.pathname);
    } catch {
      return false;
    }
  }

  function findLogicalCard(link) {
    return (
      link.closest("article") ||
      link.closest("[class*='project-card']") ||
      link.closest("[class*='mod-card']") ||
      link.closest("[class*='search-result']") ||
      link.closest("[class*='card']") ||
      link.closest("li") ||
      link.parentElement
    );
  }

  function attachCardButton(link) {
    if (!link || link.hasAttribute(LINK_MARK)) return;
    if (!isCurseForgeModLink(link) && !isArkCodesModLink(link)) return;

    const host = findLogicalCard(link);
    if (!host || host.hasAttribute(CARD_MARK)) return;

    link.setAttribute(LINK_MARK, "1");
    host.setAttribute(CARD_MARK, "1");
    host.appendChild(createButton(link, host));
  }

  function attachDetailPageButton() {
    if (document.documentElement.hasAttribute(PAGE_MARK)) return;

    const current = normalizeUrl(location.href);
    const curseForgeDetail =
      /curseforge\.com\/ark-survival-ascended\/mods\/[a-z0-9-]+$/i.test(current);
    const arkCodesDetail =
      /arkcodes\.com\/mods\/(?:\d{4,10}|[a-z0-9-]+)$/i.test(current);

    if (!curseForgeDetail && !arkCodesDetail) return;

    document.documentElement.setAttribute(PAGE_MARK, "1");

    const syntheticLink = document.createElement("a");
    syntheticLink.href = location.href;

    const preferredHost =
      document.querySelector("[class*='download']")?.parentElement ||
      document.querySelector("main h1")?.parentElement ||
      document.querySelector("h1")?.parentElement ||
      document.body;

    preferredHost.appendChild(createButton(syntheticLink, preferredHost));
  }

  function scan() {
    attachDetailPageButton();

    const links = document.querySelectorAll(
      'a[href*="/ark-survival-ascended/mods/"], a[href*="/mods/"]'
    );

    for (const link of links) {
      try {
        attachCardButton(link);
      } catch {
        // One malformed result must never break the rest of the page.
      }
    }
  }

  let timer = null;
  const observer = new MutationObserver(() => {
    clearTimeout(timer);
    timer = setTimeout(scan, 150);
  });

  observer.observe(document.documentElement, {
    subtree: true,
    childList: true
  });

  scan();
})();
