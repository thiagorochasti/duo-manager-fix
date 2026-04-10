> **Where to post:** Open a new issue at https://github.com/DuoStream/Duo/issues
> **Suggested title:** Sunshine crashes and won't start on recent Windows 11 — fix + resolution and gamepad issues resolved

---

Hey, I ran into a chain of issues with Duo Manager 1.5.6 on a recent Windows 11 build. Posting the full story here because each fix uncovered the next problem, and I ended up building a complete patch. Maybe this saves someone else a few hours.

**Repo + one-click installer:** https://github.com/thiagorochasti/duo-manager-fix

---

### Problem 1 — Sunshine loops and never starts the streaming server

On recent Windows 11 builds, the `sunshine.exe` bundled with Duo Manager enters a crash/restart loop and never actually brings the streaming server online. Moonlight shows the host as available but immediately fails to connect.

**Fix:** Replace `C:\Program Files\Duo\sunshine.exe` with the one from [Apollo 0.4.6](https://github.com/SudoMaker/Apollo) (a maintained Sunshine fork). Apollo starts cleanly, detects the GPU correctly, and on NVIDIA RTX cards it uses NVENC HEVC encoding via DXGI — which the original bundled binary also fails to do on recent Windows 11.

The installer bundles Apollo's `sunshine.exe` automatically. No separate Apollo installation needed.

---

### Problem 2 — Web management UI breaks after replacing sunshine.exe

After swapping the binary, the management UI at `https://HOST:62203` goes blank — no pairing page, no device management, nothing. This is because Duo ships HTML files that reference Vue.js bundles from a much older Sunshine version. Apollo's `sunshine.exe` expects its own, newer frontend assets that aren't present in Duo's installation folder.

**Fix:** Copy Apollo's `pin.html`, `login.html`, `welcome.html`, and the `assets/` folder into `C:\Program Files\Duo\assets\web\`. The installer does this automatically.

---

### Problem 3 — Streaming session stuck at 640×480

With the server finally running, Moonlight connects — but the remote session is always 640×480, regardless of what resolution Moonlight requests. 

Tracing the launch chain reveals that `Duo.exe` hardcodes `640 480` as arguments 5 and 6 every time it calls `DuoRdp.exe`, with no way to configure this.

**Fix:** A thin wrapper (`DuoRdpWrapper.exe`) sits in place of `DuoRdp.exe`, intercepts those two arguments, replaces any resolution below 4K with `3840×2160`, then forwards everything to the original binary (`DuoRdp_orig.exe`). Apollo's encoder downscales dynamically to whatever Moonlight actually requests (1080p, 1440p, 4K — all work).

---

### Problem 4 — Virtual gamepad visible on the host PC's Steam

When a controller is connected through Moonlight, ViGEmBus creates a virtual DS4 device visible to **all Windows sessions simultaneously** — including the physical console session. The host user's Steam detects and reacts to the remote controller.

This is a known ViGEmBus architectural limitation (no per-session isolation; the project is archived). On recent Windows 11 builds this became noticeably more disruptive.

**Fix:** A background Windows service (`DuoGamepadIsolator`) monitors HID device arrivals via `CM_Register_Notification` (zero CPU when idle). On each new ViGEmBus virtual device:

1. Identifies the physically logged-in console user via `WTSGetActiveConsoleSessionId` + `WTSQuerySessionInformation`
2. Applies a protected DACL: `DENY GENERIC_ALL` for the console user, `ALLOW GENERIC_ALL` for `EVERYONE`
3. Calls `CM_Disable_DevNode` + `CM_Enable_DevNode` to close any handles Steam already opened before the DACL was applied

Result: the streaming session uses the controller normally; the host PC's Steam never sees it. Works for multiple simultaneous streaming users.

---

Full source in C# (.NET Framework 4.x, no extra dependencies), Inno Setup installer script, and a ready-to-run installer on the [releases page](https://github.com/thiagorochasti/duo-manager-fix/releases). Happy to answer questions or take PRs.
