# Duo Manager Fix

The ultimate community patch for **[Duo Manager 1.5.6](https://github.com/DuoStream/Duo/releases/tag/v1.5.6)**. This fix resolves critical issues that appear on recent Windows 11 builds with NVIDIA RTX GPUs and [Moonlight](https://moonlight-stream.org/) streaming.

> **Symptoms Fixed:** Moonlight fails to connect immediately, sessions stuck at 640×480, management UI showing a black/blank screen, 4K host overload during first connection, or audio routing to the wrong device.

---

## Quick Install (v1.0.7)

1. Ensure **[Duo Manager 1.5.6](https://github.com/DuoStream/Duo/releases/tag/v1.5.6)** is installed.
2. Ensure **[ViGEmBus](https://github.com/nefarius/ViGEmBus/releases/latest)** is installed.
3. Download **[DuoManagerFix-Setup.exe](https://github.com/thiagorochasti/duo-manager-fix/releases/latest)**.
4. Run the installer (it automatically requests Admin privileges).
5. Choose your engine (**Apollo** for stability, **Sunshine** for experimental feature support).
6. Connect from Moonlight and enjoy!

---

## What it fixes

### 1 — Streaming server crashes (Apollo Engine)
On recent Windows 11 builds, the original `sunshine.exe` bundled with Duo Manager often enters a crash loop.
**Fix:** The installer replaces it with a specialized version from **Apollo 0.4.6**, ensuring stable connections and proper NVENC encoding on RTX cards.

### 2 — Resolution stuck at 640×480 (real-time, no restart needed)
Duo Manager hardcodes `640×480` into its internal RDP component and ignores the resolution Moonlight actually requested.

**Fix:** Our **DuoRdpWrapper** intercepts these arguments and passes the correct resolution to the RDP client. It reads the exact resolution from the Apollo/Sunshine debug log and updates in real-time — when you change resolution in Moonlight and reconnect, the wrapper detects it automatically and restarts the RDP session at the new resolution **without restarting Duo Manager Service**.

> **Requirement:** Set `min_log_level = debug` in your Apollo/Sunshine config (`localhost:47990 → Configuration → General`) for the best resolution detection accuracy.

### 3 — Web management UI blank
The management page (`https://YOUR_PC:62203`) often appears blank because of outdated Vue.js assets.
**Fix:** The installer replaces the web assets with up-to-date files matching the engine you selected.

### 4 — Dual Engine Support
During installation, you can choose:
- **Apollo 0.4.6:** Recommended for 99% of users. Solid stability and performance.
- **Sunshine Native:** Best for users testing new HID features or specific controller drivers.

### 5 — Auto-Admin Installer
No more "Run as Administrator" right-click requirement. The installer handles elevation automatically to ensure every patch is applied correctly.

### 6 — Audio routed to wrong device / no audio (Duo 1.5.6 only)
`Duo.exe` hardcodes `virtual_sink = Remote Audio` internally and injects it into the Apollo/Sunshine config on every launch, silently overriding whatever audio sink you configured.

**Fix:** The installer patches `Duo.exe` (Duo 1.5.6 only) to remove this hardcoded override, letting Apollo/Sunshine use the audio sink you actually configured.

> If after installing you lose audio, set the value manually: open `localhost:47990 → Configuration → Audio/Video → Virtual Sink → Remote Audio` and restart the service.

---

## Verifying the fix

After connecting from Moonlight, check `C:\Users\Public\duordp_args.txt`:

**Resolution correctly applied:**
```
=> Duo sent 640x480. Overriding with 2560x1440 [Moonlight (GET /launch mode=)]
=> Calling: C:\Program Files\Duo\DuoRdp_orig.exe "127.0.0.1" ... "2560" "1440"
```

**Real-time resolution change detected:**
```
=== Resolution change detected: 1920x1080 -> 2560x1440. Restarting DuoRdp_orig.exe.
=> Calling: C:\Program Files\Duo\DuoRdp_orig.exe "127.0.0.1" ... "2560" "1440"
```

---

## Troubleshooting

**Resolution still wrong?**
- Confirm `min_log_level = debug` is set in Apollo/Sunshine config.
- Check `duordp_args.txt` — the log will show which source was used (or why detection failed).

**No audio / audio on wrong device?**
- Open `http://localhost:47990 → Configuration → Audio/Video`.
- Set **Virtual Sink** to `Remote Audio` and restart the service.
- See also: [#469](https://github.com/DuoStream/Duo/issues/469), [#478](https://github.com/DuoStream/Duo/issues/478).

**Moonlight fails to connect / Black screen**
- Check the Apollo/Sunshine log in `C:\Program Files\Duo\config\` for encoder errors (the log file name matches your `sunshine_name` setting, e.g. `Games.log`, `cosmo.log`).
- Ensure GPU drivers are current.
- Restart the "Duo Manager" service in `services.msc`.

**Apps opening on the host instead of the remote session?**
Do **not** enable Process Patching or set Targeted Applications = All in Duo Manager → Patch Settings. These options cause known issues on Windows — see [#458](https://github.com/DuoStream/Duo/issues/458) and [#446](https://github.com/DuoStream/Duo/issues/446). Instead, log in as the user on the remote session and open the application manually from within the session.

---

## Building from source

1. `git clone https://github.com/thiagorochasti/duo-manager-fix.git`
2. Run `scripts\build.bat` (Requires .NET 4.x).
3. Ensure Apollo/Sunshine binaries are in `bundled/`.
4. Open `installer\setup.iss` in **Inno Setup 6** and compile (F9).

---

## License
MIT
