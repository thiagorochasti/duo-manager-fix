> **Where to post:** Open a new issue at https://github.com/DuoStream/Duo/issues
> **Suggested title:** Fix for 640×480 resolution, broken web UI, and gamepad bleeding — recent Windows 11 builds + RTX GPUs

---

Hey, I ran into three issues with Duo Manager 1.5.6 + Moonlight on recent builds of Windows 11 with an NVIDIA RTX GPU. These problems did not occur on older Windows 11 versions and appear to have been triggered by changes introduced in recent Windows 11 updates.

I ended up building a complete fix and am posting it here in case others hit the same problems.

**Repo + one-click installer:** https://github.com/thiagorochasti/duo-manager-fix

No extra software required beyond Duo Manager itself and ViGEmBus — everything else is bundled in the installer.

---

### Problem 1 — Resolution hardcoded to 640×480

`Duo.exe` always passes `640 480` as arguments 5 and 6 when launching `DuoRdp.exe`, regardless of what Moonlight actually requests. Every session starts at 640×480 on recent Windows 11 builds, even when Moonlight is configured for 1080p, 1440p, or 4K.

**Fix:** A wrapper (`DuoRdpWrapper.exe`) replaces `DuoRdp.exe`, intercepts those arguments, substitutes `3840×2160`, and forwards to the original binary. The streaming engine then downscales dynamically to whatever resolution Moonlight requests.

---

### Problem 2 — Web management UI blank after switching to Apollo

The Sunshine version bundled with Duo Manager fails to detect NVIDIA RTX 50-series GPUs via DXGI on recent Windows 11 builds. Replacing it with Apollo's `sunshine.exe` fixes GPU encoding, but the management UI at `https://HOST:62203` breaks entirely — blank pages, no pairing flow — because Duo's bundled HTML files reference Vue.js assets that only Apollo ships.

**Fix:** Replace the HTML files and the `assets/` folder in `C:\Program Files\Duo\assets\web\` with Apollo's versions. The installer handles this automatically.

---

### Problem 3 — Virtual gamepad visible on the host PC's Steam

When a controller is connected through Moonlight, ViGEmBus creates a virtual DS4 device that is **visible to all Windows sessions simultaneously** — including the physical console session. On recent Windows 11 builds this became noticeably worse: the host user's Steam detects and actively reacts to the remote controller, interfering with local use of the PC during a streaming session.

This is a known ViGEmBus architectural limitation (no per-session device isolation; the project is archived with no plans to fix it).

**Fix:** A background Windows service (`DuoGamepadIsolator`) monitors HID device arrivals via `CM_Register_Notification` (zero CPU overhead when idle). When it detects a new ViGEmBus virtual device:

1. Identifies the physically logged-in user via `WTSGetActiveConsoleSessionId` + `WTSQuerySessionInformation`
2. Applies a protected DACL: `DENY GENERIC_ALL` for the console user, `ALLOW GENERIC_ALL` for `EVERYONE`
3. Calls `CM_Disable_DevNode` + `CM_Enable_DevNode` to invalidate any handles Steam already opened before the DACL was applied

Result: the streaming session uses the controller normally; the host PC's Steam never sees it. Supports multiple simultaneous streaming users without interference.

---

Full source in C# (.NET Framework 4.x, no extra dependencies), Inno Setup installer script, and a ready-to-run installer on the releases page. Happy to answer questions or take PRs.
