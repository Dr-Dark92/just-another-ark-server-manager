(() => {
  const MARK = "data-jaasm-button";

  function extractNumericIdFromUrl(url) {
    const arkCodes = url.match(/arkcodes\.com\/mods\/(\d{4,10})(?:\/|$)/i);
    if (arkCodes) return arkCodes[1];

    const queryId = url.match(/[?&](?:projectId|modId|id)=(\d{4,10})(?:&|$)/i);
    if (queryId) return queryId[1];

    return null;
  }

  function extractProjectIdFromText(text) {
    const match = String(text || "").match(/Project\s*ID\s*:?\s*(\d{4,10})/i);
    return match ? match[1] : null;
  }

  async function resolveCurseForgeId(link) {
    const nearby =
      extractProjectIdFromText(link.closest("article")?.innerText) ||
      extractProjectIdFromText(link.parentElement?.innerText);

    if (nearby) return nearby;

    if (location.href === link.href || location.href.replace(/\/$/, "") === link.href.replace(/\/$/, "")) {
      return extractProjectIdFromText(document.body?.innerText);
    }

    try {
      const response = await fetch(link.href, {
        credentials: "include",
        cache: "no-store"
      });

      if (!response.ok) return null;
      const html = await response.text();

      const textMatch = extractProjectIdFromText(html);
      if (textMatch) return textMatch;

      const jsonish = html.match(/["'](?:projectId|id)["']\s*:\s*(\d{4,10})/i);
      return jsonish ? jsonish[1] : null;
    } catch {
      return null;
    }
  }

  async function resolveModId(link) {
    const numeric = extractNumericIdFromUrl(link.href);
    if (numeric) return numeric;

    if (/curseforge\.com$/i.test(link.hostname) &&
        /\/ark-survival-ascended\/mods\//i.test(link.pathname)) {
      return await resolveCurseForgeId(link);
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
        button.textContent = response?.status === 409 ? "Already added" : "Add failed";
        button.title = response?.message || "JAASM could not add this mod.";
        button.disabled = false;
      }
    );
  }

  function attachButton(link) {
    if (!link || link.hasAttribute(MARK)) return;

    const isCurseForge =
      /curseforge\.com$/i.test(link.hostname) &&
      /\/ark-survival-ascended\/mods\/[a-z0-9-]+/i.test(link.pathname);

    const isArkCodes =
      /arkcodes\.com$/i.test(link.hostname) &&
      /\/mods\/\d{4,10}\//i.test(link.pathname);

    if (!isCurseForge && !isArkCodes) return;

    link.setAttribute(MARK, "1");

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

      const modId = await resolveModId(link);
      if (!modId) {
        button.dataset.state = "error";
        button.textContent = "ID not found";
        button.title = "Open the mod page and try again.";
        button.disabled = false;
        return;
      }

      sendToJaasm(modId, button);
    });

    const host =
      link.closest("article") ||
      link.closest("[class*='project-card']") ||
      link.closest("[class*='card']") ||
      link.parentElement;

    if (!host) return;

    const existing = host.querySelector(".jaasm-add-mod-button");
    if (!existing) {
      host.appendChild(button);
    }
  }

  function scan() {
    const links = document.querySelectorAll(
      'a[href*="/ark-survival-ascended/mods/"], a[href*="/mods/"]'
    );

    for (const link of links) {
      try {
        attachButton(link);
      } catch {
        // One malformed card must never break injection on the rest of the page.
      }
    }
  }

  let timer = null;
  const observer = new MutationObserver(() => {
    clearTimeout(timer);
    timer = setTimeout(scan, 120);
  });

  observer.observe(document.documentElement, {
    subtree: true,
    childList: true
  });

  scan();
})();
