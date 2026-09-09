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

    let response = null;

    for (let attempt = 0; attempt < 8; attempt++) {
      try {
        response = await chrome.tabs.sendMessage(tabId, {
          type: "JAASM_GET_PAGE_MOD_ID",
          provider
        });
      } catch {
        response = null;
      }

      if (response?.modId) {
        return { ok: true, modId: response.modId };
      }

      await new Promise((resolve) => setTimeout(resolve, 500));
    }

    return {
      ok: false,
      message: provider === "curseforge"
        ? "Rendered page did not expose a Project ID after retries."
        : "Rendered page did not expose a Mod ID after retries."
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
