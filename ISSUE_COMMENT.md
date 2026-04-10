# Issue Comment — Duo Manager Fix

> Post this as a comment on a Duo Manager issue, or open a new one at:
> https://github.com/DuoStream/Duo/issues
> Suggested title: **"Fix for 640x480 resolution, broken web UI, and gamepad bleeding with Apollo on RTX GPUs"**

---

Hey, I ran into several issues with Duo Manager 1.5.6 when pairing it with Apollo for RTX GPU support and Moonlight streaming. I ended up building a complete fix and have published it as an open-source tool:

**→ https://github.com/thiagorochasti/duo-manager-fix**

Here's what it addresses:

---

### Problem 1 — Hardcoded 640×480 resolution

`Duo.exe` always passes `640 480` as arguments 5 and 6 when launching `DuoRdp.exe`, regardless of the client's actual resolution. This makes every Moonlight session start at 640×480.

**Fix:** A thin wrapper `DuoRdpWrapper.exe` sits in place of `DuoRdp.exe`. It intercepts the arguments, replaces any resolution below 4K with `3840 2160`, and forwards everything to the real `DuoRdp_orig.exe`. Apollo then handles dynamic downscaling to whatever resolution Moonlight actually requests.

---

### Problem 2 — Web management UI broken after Apollo integration

When replacing Duo's `sunshine.exe` with Apollo's version, the management UI at `https://HOST:62203` breaks — blank pages, missing Device Management, no pairing flow. This is because the HTML files in `C:\Program Files\Duo\assets\web\` reference Vue.js bundles that only Apollo ships.

**Fix:** Copy Apollo's `pin.html`, `login.html`, `welcome.html`, and the entire `assets/` folder into Duo's web directory.

---

### Problem 3 — Virtual gamepads (ViGEmBus DS4) visible to the console session

This is the trickiest one. When Moonlight + Apollo create virtual DS4 controllers via ViGEmBus, the host user's Steam also sees and reacts to those controllers — the console session can't be isolated at the ViGEmBus level because ViGEmBus 1.21/1.22 creates devices **globally** (no per-session isolation).

**Fix:** A Windows service `DuoGamepadIsolator` monitors HID device arrivals via `CM_Register_Notification` (zero CPU overhead in idle). When it detects a ViGEmBus virtual device:

1. It identifies the console session user via `WTSGetActiveConsoleSessionId` + `WTSQuerySessionInformation`
2. Applies a protected DACL: `DENY GENERIC_ALL` for the console user + `ALLOW GENERIC_ALL` for `EVERYONE`
3. Calls `CM_Disable_DevNode` + `CM_Enable_DevNode` to force-close any handles already opened by Steam

This means:
- The streaming session (Games user via RDP) can use the controller normally
- The console user's Steam no longer sees it
- Multiple streaming users each get their own controllers without interfering with each other or the host

---

### Why we built this

We're using Duo Manager 1.5.6 as the session management layer (user creation, RDP tunneling) with Apollo as the GPU encoder — specifically because Apollo finds NVIDIA RTX 50-series GPUs via DXGI/NVENC where the original Sunshine in Duo Manager fails. The combination works great once these three issues are resolved.

---

The repo includes full source code (C#, no dependencies beyond .NET Framework 4.x), an Inno Setup installer script, and step-by-step documentation. Happy to answer questions or accept PRs if you find edge cases.
