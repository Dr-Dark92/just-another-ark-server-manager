const BRIDGE = "http://127.0.0.1:8485";

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (!message || message.type !== "JAASM_ADD_MOD") {
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
    .catch((error) => {
      sendResponse({
        ok: false,
        status: 0,
        message: "JAASM is not reachable on 127.0.0.1:8485. Start JAASM first."
      });
    });

  return true;
});
