(() => {
  const CARD_MARK = "data-jaasm-card";
  const PAGE_MARK = "data-jaasm-detail-button";

  function extractProjectIdFromText(text) {
    const match = String(text || "").match(/Project\s*ID\s*:?\s*(\d{4,10})/i);
    return match ? match[1] : null;
  }

  function extractModIdFromText(text) {
    const match = String(text || "").match(/Mod\s*ID\s*:?\s*(\d{4,10})/i);
    return match ? match[1] : null;
  }

  function extractArkCodesIdFromUrl(url) {
    const match = String(url || "").match(/arkcodes\.com\/mods\/(\d{4,10})(?:\/|$)/i);
    return match ? match[1] : null;
  }

  function providerForUrl(url) {
    try {
      const u = new URL(url, location.href);
      if (/curseforge\.com$/i.test(u.hostname)) return "curseforge";
      if (/arkcodes\.com$/i.test(u.hostname)) return "arkcodes";
    } catch {}
    return null;
  }

  async function resolveRenderedId(url, provider) {
    return await new Promise((resolve) => {
      chrome.runtime.sendMessage(
        { type: "JAASM_RESOLVE_RENDERED_ID", url, provider },
        (response) => {
          if (chrome.runtime.lastError) {
            resolve(null);
            return;
          }

          resolve(response?.ok ? response.modId : null);
        }
      );
    });
  }

  async function resolveModId(url, nearbyText = "") {
    const provider = providerForUrl(url);
    if (!provider) return null;

    if (provider === "arkcodes") {
      return (
        extractModIdFromText(nearbyText) ||
        extractArkCodesIdFromUrl(url) ||
        await resolveRenderedId(url, provider)
      );
    }

    return (
      extractProjectIdFromText(nearbyText) ||
      await resolveRenderedId(url, provider)
    );
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

  function makeButton(url, nearbyTextProvider) {
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

      const nearbyText = typeof nearbyTextProvider === "function"
        ? nearbyTextProvider()
        : String(nearbyTextProvider || "");

      const modId = await resolveModId(url, nearbyText);

      if (!modId) {
        button.dataset.state = "error";
        button.textContent = "ID not found";
        button.title =
          "JAASM crawled one rendered page deeper but could not find Project ID / Mod ID.";
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
        /^\/mods\/\d{4,10}(?:\/[a-z0-9-]+)?\/?$/i.test(u.pathname);
    } catch {
      return false;
    }
  }

  function findCard(link) {
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
    if (!link || (!isCurseForgeModLink(link) && !isArkCodesModLink(link))) return;

    const card = findCard(link);
    if (!card || card.hasAttribute(CARD_MARK)) return;

    card.setAttribute(CARD_MARK, "1");
    card.classList.add("jaasm-card-host");

    const button = makeButton(link.href, () => card.innerText || "");
    button.classList.add("jaasm-card-button");
    card.appendChild(button);
  }

  function findElementContainingLabel(labelRegex) {
    const nodes = document.querySelectorAll("div,span,p,li,dd,dt,strong");
    for (const node of nodes) {
      const text = node.textContent?.trim() || "";
      if (labelRegex.test(text) && /\d{4,10}/.test(text)) {
        return node;
      }
    }
    return null;
  }

  function attachDetailButton() {
    if (document.documentElement.hasAttribute(PAGE_MARK)) return;

    const current = location.href;
    const provider = providerForUrl(current);
    if (!provider) return;

    const isCurseForgeDetail =
      provider === "curseforge" &&
      /\/ark-survival-ascended\/mods\/[a-z0-9-]+\/?(?:[?#].*)?$/i.test(current);

    const isArkCodesDetail =
      provider === "arkcodes" &&
      /\/mods\/\d{4,10}(?:\/[a-z0-9-]+)?\/?(?:[?#].*)?$/i.test(current);

    if (!isCurseForgeDetail && !isArkCodesDetail) return;

    let target = provider === "curseforge"
      ? findElementContainingLabel(/Project\s*ID/i)
      : findElementContainingLabel(/Mod\s*ID/i);

    if (!target) {
      const all = document.querySelectorAll("body *");
      for (const node of all) {
        const text = node.textContent?.trim() || "";
        const match = provider === "curseforge"
          ? /Project\s*ID\s*:?\s*\d{4,10}/i.test(text)
          : /Mod\s*ID\s*:?\s*\d{4,10}/i.test(text);

        if (match && node.children.length <= 4) {
          target = node;
          break;
        }
      }
    }

    if (!target) return;

    document.documentElement.setAttribute(PAGE_MARK, "1");

    const button = makeButton(current, () => target.parentElement?.innerText || target.innerText || "");
    button.classList.add("jaasm-inline-button");
    target.insertAdjacentElement("afterend", button);
  }

  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (!message || message.type !== "JAASM_GET_PAGE_MOD_ID") return;

    const provider = String(message.provider || "");
    const text = document.body?.innerText || "";

    const modId = provider === "curseforge"
      ? extractProjectIdFromText(text)
      : extractModIdFromText(text) || extractArkCodesIdFromUrl(location.href);

    sendResponse({ modId: modId || null });
  });

  function scan() {
    attachDetailButton();

    const links = document.querySelectorAll(
      'a[href*="/ark-survival-ascended/mods/"], a[href*="arkcodes.com/mods/"], a[href^="/mods/"]'
    );

    for (const link of links) {
      try {
        attachCardButton(link);
      } catch {
        // Ignore malformed result cards.
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
