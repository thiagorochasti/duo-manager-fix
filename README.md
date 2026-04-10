# Duo Manager Fix

Fixes three bugs in **[Duo Manager 1.5.6](https://github.com/DuoStream/Duo/releases/tag/v1.5.6)** for NVIDIA RTX GPUs + [Moonlight](https://moonlight-stream.org/) streaming.

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

### 1 — Resolution stuck at 640×480

Duo Manager hardcodes `640 480` in the arguments it passes to its internal RDP component, regardless of what Moonlight requests.

**Fix:** A wrapper intercepts those arguments and replaces any resolution below 4K with `3840×2160`. Apollo/Sunshine then dynamically downscales to whatever Moonlight actually asks for (1080p, 1440p, 4K — all work).

### 2 — Web management UI broken

The management page at `https://YOUR_PC:62203` shows a blank page or errors because Duo Manager ships with an outdated version of the streaming engine that is missing the assets required by the current UI.

**Fix:** The installer replaces the HTML and JavaScript files with the correct versions that match the streaming engine bundled in this package.

### 3 — Controller leaking into host Steam

When you connect a gamepad through Moonlight, your physical PC's Steam also detects that controller and reacts to it — because the virtual gamepad driver (ViGEmBus) creates devices globally, visible to all Windows sessions simultaneously.

**Fix:** A background Windows service (`DuoGamepadIsolator`) monitors for new virtual gamepad devices. When one appears, it:
1. Identifies the physically logged-in user on the PC
2. Applies a permission rule (DACL) that blocks only that user from accessing the device
3. Forces a device reset so any handles Steam already had are closed

Result: the streaming session uses the controller normally; the host PC's Steam never sees it.

---

## Verifying the fix

After connecting from Moonlight, check each fix:

**Resolution**
Open `C:\Users\Public\duordp_args.txt` — you should see:
```
=> Substituindo 640x480 por 3840x2160
```

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

**Controller still appears on host Steam**
- Run `sc query DuoGamepadIsolator` — service must be `RUNNING`
- The service must be running *before* you connect Moonlight
- Check `C:\Users\Public\duo_isolator.log` for errors

**Web UI blank page**
- Clear browser cache and retry
- Check `C:\Program Files\Duo\assets\web\assets\` — should contain `.js` files

**Black screen in Moonlight**
- Check `C:\Program Files\Duo\config\Games.log` for encoder errors
- Make sure your GPU drivers are up to date

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
