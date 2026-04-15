# Duo Manager Fix

The ultimate community patch for **[Duo Manager 1.5.6](https://github.com/DuoStream/Duo/releases/tag/v1.5.6)**. This fix resolves critical issues that appear on recent Windows 11 builds with NVIDIA RTX GPUs and [Moonlight](https://moonlight-stream.org/) streaming.

> **Symptoms Fixed:** Moonlight fails to connect immediately, sessions stuck at 640×480, management UI showing a black/blank screen, or 4K host overload during first connection.

---

## Quick Install (v1.0.6)

1.  Ensure **[Duo Manager 1.5.6](https://github.com/DuoStream/Duo/releases/tag/v1.5.6)** is installed.
2.  Ensure **[ViGEmBus](https://github.com/nefarius/ViGEmBus/releases/latest)** is installed.
3.  Download **[DuoManagerFix-Setup.exe](https://github.com/thiagorochasti/duo-manager-fix/releases/latest)**.
4.  Run the installer (it automatically requests Admin privileges).
5.  Choose your engine (**Apollo** for stability, **Sunshine** for experimental feature support).
6.  Connect from Moonlight and enjoy!

---

## What it fixes

### 1 — Streaming server crashes (Apollo Engine)
On recent Windows 11 builds, the original `sunshine.exe` bundled with Duo Manager often enters a crash loop. 
**Fix:** The installer replaces it with a specialized version from **Apollo 0.4.6**, ensuring stable connections and proper NVENC encoding on RTX cards.

### 2 — Resolution stuck at 640×480
Duo Manager hardcodes `640 480` into its internal RDP component, ignores Moonlight's requested resolution.
**Fix:** Our **DuoRdpWrapper** intercepts these arguments and intelligently matches your client's screen size. 
*   **Predictive Match:** It reads your previous session's resolution from `Games.log`.
*   **Safe Start:** New installs default to `1920x1080` (fixing the old "4K host lock" bug).
*   *Note: To sync a new screen size, simply connect once, close the stream, and reconnect.*

### 3 — Web management UI blank
The management page (`https://YOUR_PC:62203`) often appears blank because of outdated Vue.js assets. 
**Fix:** The installer replaces the web assets with up-to-date files matching the engine you selected.

### 4 — Dual Engine Support
During installation, you can choose:
*   **Apollo 0.4.6:** Recommended for 99% of users. Solid stability and performance.
*   **Sunshine Native:** Best for users testing new HID features or specific controller drivers.

### 5 — Auto-Admin Installer
No more "Run as Administrator" right-click requirement. The installer handles elevation automatically to ensure every patch is applied correctly.

### 6 — Seamless PIN Pairing
We patched the pairing process via the Web UI to automatically grant full administrative permissions to new Moonlight clients, bypassing manual UAC prompts on the host PC.

---

## Verifying the fix

After connecting from Moonlight:

**Streaming server**
Moonlight connects successfully and displays the Desktop or Steam Big Picture.

**Resolution**
Check `C:\Users\Public\duordp_args.txt` for the log line:
```
=> Duo sent 640x480 (bug). Replacing with 2560x1440 [Moonlight (Games.log)]
```

**Web UI**
Visit `https://YOUR_PC_IP:62203` — the management page should be fully functional.

---

## Troubleshooting

**Resolution still low on first connection?**
RDP locks resolution mid-session. Simply connect, wait for the desktop to load, end the session, and connect again. The wrapper will now have "learned" your resolution and will apply it instantly.

**Moonlight fails to connect / Black screen**
- Check `C:\Program Files\Duo\config\Games.log` for encoder errors.
- Ensure GPU drivers are current.
- Restart the "Duo Manager" service in `services.msc`.

---

## Building from source

1.  `git clone https://github.com/thiagorochasti/duo-manager-fix.git`
2.  Run `scripts\build.bat` (Requires .NET 4.x).
3.  Ensure Apollo/Sunshine binaries are in `bundled/`.
4.  Open `installer\setup.iss` in **Inno Setup 6** and compile (F9).

---

## License
MIT
