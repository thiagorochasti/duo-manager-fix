> **Where to post:** Open a new issue at https://github.com/DuoStream/Duo/issues
> **Suggested title:** Fix for 640×480 resolution, broken web UI, and gamepad bleeding with Apollo on RTX GPUs

---

Hey, I ran into three issues with Duo Manager 1.5.6 + Moonlight on an NVIDIA RTX GPU and ended up building a complete fix. Posting here in case others hit the same problems.

**Repo + one-click installer:** https://github.com/thiagorochasti/duo-manager-fix

No extra software required beyond Duo Manager itself and ViGEmBus — everything else is bundled.

---

### Problem 1 — Resolution hardcoded to 640×480

`Duo.exe` always passes `640 480` as arguments 5 and 6 when launching `DuoRdp.exe`, regardless of what Moonlight requests. Every session starts at 640×480.

**Fix:** A wrapper (`DuoRdpWrapper.exe`) replaces `DuoRdp.exe`, intercepts those arguments, substitutes `3840×2160`, and forwards to the original binary. The streaming engine then downscales dynamically to whatever resolution Moonlight actually asks for.

---

### Problem 2 — Web management UI blank after switching to Apollo

The original Sunshine bundled with Duo Manager doesn't support RTX 50-series GPUs (fails to find the device via DXGI). Replacing it with Apollo's `sunshine.exe` fixes encoding, but the management UI at `https://HOST:62203` breaks — blank pages, no pairing flow — because Duo's HTML files reference Vue.js bundles that only Apollo ships.

**Fix:** Replace the HTML files and assets in `C:\Program Files\Duo\assets\web\` with Apollo's versions. The installer handles this automatically.

---

### Problem 3 — Virtual gamepad visible on the host PC's Steam

When a controller is connected through Moonlight, ViGEmBus creates a virtual DS4 device that's **visible to all Windows sessions simultaneously** — including the console session. The host user's Steam detects and reacts to the remote controller.

This is a known ViGEmBus limitation (no per-session isolation; the project is archived).

**Fix:** A Windows service (`DuoGamepadIsolator`) monitors HID device arrivals via `CM_Register_Notification` (zero CPU in idle). On each new virtual device:

1. Detects the physically logged-in user via `WTSGetActiveConsoleSessionId` + `WTSQuerySessionInformation`
2. Applies a protected DACL: `DENY GENERIC_ALL` for the console user, `ALLOW GENERIC_ALL` for `EVERYONE`
3. Calls `CM_Disable_DevNode` + `CM_Enable_DevNode` to invalidate any handles Steam already opened before the DACL was set

Result: the streaming session uses the controller normally; the host PC's Steam never sees it. Works for multiple simultaneous streaming users.

---

Full source in C# (.NET Framework 4.x, no extra dependencies), Inno Setup script, and a ready-to-run installer in the releases page. Happy to answer questions or take PRs.
