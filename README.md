# Duo Manager Fix

Fixes four issues in **[Duo Manager 1.5.6](https://github.com/DuoStream/Duo/releases/tag/v1.5.6)** that appear on recent Windows 11 builds with NVIDIA RTX GPUs + [Moonlight](https://moonlight-stream.org/) streaming.

> **Symptoms:** Moonlight shows the host as available but immediately fails to connect, session stuck at 640×480, management UI blank, or remote controller affecting the host PC's Steam.

---

## Install

**You need:**
- [Duo Manager 1.5.6](https://github.com/DuoStream/Duo/releases/tag/v1.5.6) installed
- [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases/latest) installed (virtual gamepad driver)

**Steps:**
1. Install the two prerequisites above
2. Download **[DuoManagerFix-Setup.exe](https://github.com/thiagorochasti/duo-manager-fix/releases/latest)** and run as Administrator
3. Follow the wizard — it takes about 30 seconds
4. Connect from Moonlight and play

That's it. No other software required.

---

## What it fixes

### 1 — Sunshine crashes and the streaming server never starts

On recent Windows 11 builds, the `sunshine.exe` bundled with Duo Manager enters a crash/restart loop and never brings the streaming server online. Moonlight finds the host but immediately fails to connect.

On NVIDIA RTX GPUs this is compounded by the bundled binary also failing to detect the GPU via DXGI, so even when it does start it cannot encode.

**Fix:** The installer replaces `sunshine.exe` with a version from [Apollo 0.4.6](https://github.com/SudoMaker/Apollo) — a maintained fork that starts cleanly on recent Windows 11 and uses NVENC HEVC encoding on RTX cards. No separate Apollo installation needed; it is bundled.

### 2 — Resolution stuck at 640×480

Once the server is running, Moonlight connects but the session is always 640×480 regardless of what resolution Moonlight requests. Duo Manager hardcodes `640 480` in the arguments it passes to its internal RDP component every time.

**Fix:** A wrapper intercepts those arguments and dynamically applies the correct resolution matching the client. It checks (in priority order):
1. The resolution explicitly requested by Moonlight during the previous session (reading via `Games.log`).
2. An explicit override in the Apollo `sunshine.conf` (`dd_manual_resolution`).
3. A safe fallback to `1920×1080` to prevent overloading the host GPU unnecessarily (previously 4K was forced, tanking framerates).

### 3 — Web management UI blank

After the streaming engine is replaced, the management page at `https://YOUR_PC:62203` shows a blank page. This happens because Duo's bundled HTML files reference Vue.js assets from an older Sunshine version that are no longer present.

**Fix:** The installer replaces the HTML and JavaScript files in Duo's web folder with the versions that match the streaming engine bundled in this package.

### 4 — Remote controller leaking into host Steam

When a gamepad is connected through Moonlight, the host PC's Steam also detects it and reacts — because ViGEmBus creates virtual devices globally, visible to all Windows sessions simultaneously. On recent Windows 11 builds this became noticeably more disruptive.

**Fix:** A background Windows service (`DuoGamepadIsolator`) monitors for new virtual gamepad devices. When one appears, it:
1. Identifies the physically logged-in user on the PC
2. Applies a permission rule (DACL) that blocks only that user from accessing the device
3. Forces a device reset so any handles Steam already held are closed

Result: the streaming session uses the controller normally; the host PC's Steam never sees it.

### 5 — Installer requires manual elevation

Previously, installing the fix required manually right-clicking and selecting "Run as Administrator", which led to failed installations if forgotten.

**Fix:** The installer now automatically requests Administrator privileges (UAC prompt) when double-clicked.

### 6 — PIN Pairing requires host intervention

When pairing a new Moonlight client, Apollo was failing to grant proper administrative permissions seamlessly, requiring the user to interact with the host PC.

**Fix:** The pairing process via the web UI has been patched to automatically grant full permissions to the new client without any manual UAC intervention.

---

## Verifying the fix

After connecting from Moonlight, check each fix:

**Streaming server**
Moonlight should connect successfully and show the Desktop or Steam Big Picture app.

**Resolution**
Open `C:\Users\Public\duordp_args.txt` — you should see a line confirming the fix:
```
=> Duo enviou 640x480 (bug). Substituindo por 2560x1440 [Moonlight (Games.log)]
```
*(On your very first run ever, it may fall back safely to 1920x1080).*

**Web UI**
Open `https://YOUR_PC_IP:62203` in a browser → you should see the management page with Pair/Devices tabs.

**Gamepad isolation**
On your PC, open Steam → Settings → Controller → the Moonlight controller should not appear. In the remote session, it works normally in games.

Check the service log at `C:\Users\Public\duo_isolator.log`:
```
DACL OK (DENY console + ALLOW EVERYONE).
Reciclo: Disable=0 Enable=0 OK
```

---

## Pairing Moonlight

After installing:

1. Open `https://YOUR_PC_IP:62203` in a browser
2. Go to **Pin** and enter the PIN shown on your Moonlight client
3. Apps available: **Desktop** and **Steam Big Picture** (pre-configured)

---

## Uninstalling

To remove everything this installer changed:

```cmd
:: Remove the gamepad isolation service
"C:\Program Files\DuoFix\DuoGamepadIsolator.exe" --uninstall

:: Restore original Duo binaries
copy /y "C:\Program Files\Duo\DuoRdp_orig.exe"    "C:\Program Files\Duo\DuoRdp.exe"
copy /y "C:\Program Files\Duo\sunshine_orig.exe"   "C:\Program Files\Duo\sunshine.exe"
```

Or use **Add/Remove Programs** → "Duo Manager Fix" (handles the service automatically).

---

## Troubleshooting

**Moonlight fails to connect / black screen**
- Check `C:\Program Files\Duo\config\Games.log` for encoder errors
- Make sure your GPU drivers are up to date
- Restart the Duo Manager service after installing

**Controller still appears on host Steam**
- Run `sc query DuoGamepadIsolator` — service must be `RUNNING`
- The service must be running *before* you connect Moonlight
- Check `C:\Users\Public\duo_isolator.log` for errors

**Web UI blank page**
- Clear browser cache and retry
- Check `C:\Program Files\Duo\assets\web\assets\` — should contain `.js` files

**Resolution still low**
- If `C:\Users\Public\duordp_args.txt` doesn't exist, the wrapper isn't being called — reinstall
- Check that `C:\Program Files\Duo\DuoRdp.exe` is small (~10 KB); if it's large it's the original

---

## Building from source

If you want to compile the installer yourself:

```cmd
:: 1. Clone
git clone https://github.com/thiagorochasti/duo-manager-fix.git
cd duo-manager-fix

:: 2. Compile C# sources (requires .NET Framework 4.x)
scripts\build.bat

:: 3. Extract Apollo files needed by the installer
::    (requires Apollo 0.4.6 installed at C:\Program Files\Apollo\)
scripts\prepare_bundle.bat

:: 4. Open installer\setup.iss in Inno Setup 6 and press F9
```

---

## License

MIT
