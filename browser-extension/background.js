const BRIDGE = "http://127.0.0.1:8485";

async function resolveRenderedPageId(url, provider) {
  let tab = null;

  try {
    tab = await chrome.tabs.create({
      url,
      active: false
    });

    const tabId = tab.id;
    if (!tabId) {
      return { ok: false, message: "Could not create background tab." };
    }

    await new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        chrome.tabs.onUpdated.removeListener(listener);
        reject(new Error("Timed out waiting for mod page to load."));
      }, 15000);

      const listener = (updatedTabId, changeInfo) => {
        if (updatedTabId !== tabId || changeInfo.status !== "complete") {
          return;
        }

        clearTimeout(timeout);
        chrome.tabs.onUpdated.removeListener(listener);
        setTimeout(resolve, 700);
      };

      chrome.tabs.onUpdated.addListener(listener);
    });

    for (let attempt = 0; attempt < 10; attempt++) {
      try {
        const results = await chrome.scripting.executeScript({
          target: { tabId },
          func: (providerName) => {
            const text = document.body?.innerText || "";

            if (providerName === "curseforge") {
              const match = text.match(/Project\s*ID\s*:?\s*(\d{4,10})/i);
              return match ? match[1] : null;
            }

            const match = text.match(/Mod\s*ID\s*:?\s*(\d{4,10})/i);
            if (match) return match[1];

            const urlMatch = location.href.match(/arkcodes\.com\/mods\/(\d{4,10})(?:\/|$)/i);
            return urlMatch ? urlMatch[1] : null;
          },
          args: [provider]
        });

        const modId = results?.[0]?.result;
        if (modId) {
          return { ok: true, modId };
        }
      } catch {
        // The page may still be switching/rendering. Retry briefly.
      }

      await new Promise((resolve) => setTimeout(resolve, 450));
    }

    return {
      ok: false,
      message: provider === "curseforge"
        ? "Rendered page did not expose a Project ID after direct DOM retries."
        : "Rendered page did not expose a Mod ID after direct DOM retries."
    };
  } catch (error) {
    return {
      ok: false,
      message: error?.message || "Could not crawl the mod page."
    };
  } finally {
    if (tab?.id) {
      try {
        await chrome.tabs.remove(tab.id);
      } catch {
        // Ignore tab cleanup race.
      }
    }
  }
}

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (!message) {
    return;
  }

  if (message.type === "JAASM_RESOLVE_RENDERED_ID") {
    resolveRenderedPageId(String(message.url || ""), String(message.provider || ""))
      .then(sendResponse);
    return true;
  }

  if (message.type !== "JAASM_ADD_MOD") {
    return;
  }

  fetch(BRIDGE + "/mods/add", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-JAASM-Bridge": "1"
    },
    body: JSON.stringify({ modId: String(message.modId || "") })
  })
    .then(async (response) => {
      let body = {};
      try {
        body = await response.json();
      } catch {
        body = { message: "JAASM returned an unreadable response." };
      }

      sendResponse({
        ok: response.ok,
        status: response.status,
        message: body.message || (response.ok ? "Added to JAASM." : "JAASM rejected the mod.")
      });
    })
    .catch(() => {
      sendResponse({
        ok: false,
        status: 0,
        message: "JAASM is not reachable on 127.0.0.1:8485. Start JAASM first."
      });
    });

  return true;
});
