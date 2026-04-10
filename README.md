# Duo Manager Fix

Fixes for **[Duo Manager 1.5.6](https://github.com/DuoStream/Duo/releases/tag/v1.5.6)** when used with [Apollo](https://github.com/SudoMaker/Apollo) (RTX GPU support) and [Moonlight](https://moonlight-stream.org/) game streaming.

> **Tested with:** Duo Manager 1.5.6 · Apollo 0.4.6 (SudoMaker) · Windows 11 · RTX 5070

---

## Problems this fixes

| # | Problem | Fix |
|---|---------|-----|
| 1 | Duo Manager hardcodes **640×480** resolution for RDP sessions | `DuoRdpWrapper` intercepts the call and substitutes **3840×2160** |
| 2 | Duo Manager's **web UI is broken** after replacing sunshine.exe with Apollo's | Apollo's Vue.js assets are copied to Duo's web folder |
| 3 | Virtual gamepads (ViGEmBus DS4) created by Apollo/Moonlight **bleed into the host user's Steam** — the console user can see and be affected by the remote controller | `DuoGamepadIsolator` Windows service applies a DACL deny to the console user on every virtual HID device |

---

## Prerequisites

Install these **before** running the Duo Manager Fix installer:

1. **[Duo Manager 1.5.6](https://github.com/DuoStream/Duo/releases/tag/v1.5.6)** — installed to `C:\Program Files\Duo\`
2. **[Apollo 0.4.6](https://github.com/SudoMaker/Apollo/releases/tag/0.4.6)** — installed to `C:\Program Files\Apollo\`
   - Required if you have an NVIDIA RTX GPU (Apollo finds it via DXGI/NVENC where the original sunshine.exe fails)
3. **[ViGEmBus](https://github.com/nefarius/ViGEmBus/releases)** — virtual gamepad bus driver (1.21 or newer)
4. **[Moonlight](https://moonlight-stream.org/)** — game streaming client on any device
5. **Windows 10/11** with **.NET Framework 4.x** (pre-installed on modern Windows)
6. Admin rights on the host PC

---

## What gets installed

| Component | Location | Purpose |
|-----------|----------|---------|
| `DuoRdpWrapper.exe` | `C:\Program Files\Duo\DuoRdp.exe` (replaces original) | Intercepts RDP resolution, forces 4K |
| `DuoRdp_orig.exe` | `C:\Program Files\Duo\DuoRdp_orig.exe` | Backup of the original Duo binary |
| `sunshine.exe` (Apollo) | `C:\Program Files\Duo\sunshine.exe` (replaces original) | RTX-capable GPU encoder |
| Apollo web assets | `C:\Program Files\Duo\assets\web\` | Fixes broken management UI |
| `DuoGamepadIsolator.exe` | `C:\Program Files\DuoFix\` | Gamepad isolation Windows service |

---

## Installation

### Option A — Installer (recommended)

1. Go to the [Releases](https://github.com/thiagorochasti/duo-manager-fix/releases) page and download `DuoManagerFix-Setup.exe`
2. Right-click → **Run as Administrator**
3. Follow the wizard (it will detect Duo Manager and Apollo automatically)
4. Click **Finish** — the `DuoGamepadIsolator` service starts automatically

### Option B — Manual (from source)

```cmd
:: 1. Clone the repo
git clone https://github.com/thiagorochasti/duo-manager-fix.git
cd duo-manager-fix

:: 2. Build both binaries (requires .NET Framework 4.x — csc.exe)
scripts\build.bat

:: 3. Apply all fixes (run as Administrator)
scripts\install.bat
```

---

## Step-by-step verification

After installation, verify each fix:

### Fix 1 — Resolution
1. Connect from Moonlight on any device
2. Open `C:\Users\Public\duordp_args.txt`
3. You should see lines like `=> Substituindo 640x480 por 3840x2160`

### Fix 2 — Web UI
1. Open `https://YOUR_HOST_IP:62203` in a browser (accept the self-signed cert)
2. You should see the Apollo management page with Device Management / Pair tabs

### Fix 3 — Gamepad isolation
1. Connect a controller via Moonlight (e.g. from a phone or Steam Deck)
2. On the **host PC**, open Steam → Big Picture → Controller Settings
3. The remote controller should **not** appear there
4. On the **remote session** (Games user), the controller should work normally in games

Check the service log at `C:\Users\Public\duo_isolator.log` for:
```
DACL OK (DENY console + ALLOW EVERYONE).
Reciclo: Disable=0 Enable=0 OK
```

---

## Configuring Apollo

After installation, you need to pair your Moonlight client:

1. Open `https://YOUR_HOST_IP:62203`
2. Go to **Pin** and enter the PIN shown on your Moonlight client
3. Add your apps under **Apps** (Desktop and Steam Big Picture are pre-configured)

The Apollo config lives at `C:\Program Files\Duo\config\Games.conf`.  
The app list is at `C:\Program Files\Duo\config\Games_apps.json`.

---

## Uninstalling

**To remove the gamepad isolator service only:**
```cmd
"C:\Program Files\DuoFix\DuoGamepadIsolator.exe" --uninstall
```

**To restore original Duo binaries:**
```cmd
:: Restore original DuoRdp.exe
copy /y "C:\Program Files\Duo\DuoRdp_orig.exe" "C:\Program Files\Duo\DuoRdp.exe"

:: Restore original sunshine.exe (if you backed it up)
:: copy /y "C:\Program Files\Duo\sunshine_orig.exe" "C:\Program Files\Duo\sunshine.exe"
```

---

## How it works (technical)

### DuoRdpWrapper (resolution fix)

`Duo.exe` always passes resolution arguments `640` `480` (positions 5 and 6) when launching `DuoRdp.exe`. The wrapper:
- Intercepts those arguments before they reach the real `DuoRdp_orig.exe`
- Replaces any resolution below 3840×2160 with `3840 2160`
- Uses a **Windows Job Object** (`KILL_ON_JOB_CLOSE`) so the real RDP process dies if the wrapper is killed

### DuoGamepadIsolator (gamepad isolation)

ViGEmBus 1.21/1.22 creates virtual HID devices **globally** (no per-session isolation by design — the project is archived). Every Windows session sees every virtual controller.

The service fixes this with a two-step approach:

1. **DACL**: On every new virtual HID device arrival, it applies a protected DACL:
   - `DENY GENERIC_ALL` → console session user (detected via `WTSGetActiveConsoleSessionId` + `WTSQuerySessionInformation`)
   - `ALLOW GENERIC_ALL` → `EVERYONE` (covers RDP/streaming users, SYSTEM, etc.)

2. **Handle invalidation**: After setting the DACL, it calls `CM_Disable_DevNode` + `CM_Enable_DevNode` on the device. This forces the OS to close all open handles — so even if Steam already grabbed a handle before the DACL was applied, it can't reopen one.

**Zero-CPU idle**: uses `CM_Register_Notification` (kernel callback) instead of polling. Only activates on device events.

**Edge cases handled:**
- Service starts while a session is already active (initial scan)
- Multiple HID interfaces per device (DS4 creates Col01–Col05): only one recycle cycle per device
- Infinite recycle loop prevention: `_recycleUntil` timestamp ignores arrivals triggered by the disable/enable cycle itself
- Console user changes between sessions (re-detected on each new device)
- Polling fallback if `CM_Register_Notification` fails

---

## Troubleshooting

**Controller still visible on host Steam:**
- Check `duo_isolator.log` — look for `DACL OK` entries
- Ensure the service is running: `sc query DuoGamepadIsolator`
- The service needs to be running **before** you connect Moonlight

**Web UI shows blank page:**
- Check that Apollo web assets were copied: `dir "C:\Program Files\Duo\assets\web\assets\"`
- Try clearing browser cache

**Moonlight shows black screen:**
- Check `C:\Program Files\Duo\config\Games.log` for Apollo encoder errors
- Verify your GPU: `dxdiag` → Display tab

**Resolution is still low:**
- Open `C:\Users\Public\duordp_args.txt` — if the file doesn't exist, DuoRdpWrapper isn't being called
- Verify `C:\Program Files\Duo\DuoRdp.exe` is actually the wrapper (file size should be ~10 KB, not the original ~1 MB)

---

## License

MIT — do whatever you want with it. Pull requests welcome.
