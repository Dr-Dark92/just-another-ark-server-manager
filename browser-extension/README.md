# JAASM Mod Bridge Browser Extension

This extension adds a small **+ JAASM** button next to ARK: Survival Ascended mods on:

- CurseForge
- ArkCodes

Clicking the button sends the detected numeric mod/project ID to the JAASM desktop application through the loopback bridge at:

`http://127.0.0.1:8485`

JAASM must be running and a server profile must be active.

## Chromium / Chrome / Edge

1. Open the browser extensions page.
2. Enable Developer Mode.
3. Choose **Load unpacked**.
4. Select this `browser-extension` directory.

## Firefox

For development, open `about:debugging#/runtime/this-firefox`, choose **Load Temporary Add-on**, and select `manifest.json`.

## Security

The JAASM bridge binds only to `127.0.0.1` and rejects normal web-page origins. Requests must originate from a browser-extension origin and include the JAASM bridge marker header.
