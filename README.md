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

### 4 — Auto-Admin Installer

Previously, installing the fix required manually right-clicking and selecting "Run as Administrator", which led to failed installations if forgotten.

**Fix:** The installer now automatically requests Administrator privileges (UAC prompt) when double-clicked.

### 5 — PIN Pairing requires host intervention

When pairing a new Moonlight client, Apollo was failing to grant proper administrative permissions seamlessly, requiring the user to interact with the host PC.

**Fix:** The pairing process via the web UI has been patched to automatically grant full permissions to the new client without any manual UAC intervention.

### 6 — Dual Engine Support (Apollo vs Sunshine)

You can now choose which streaming engine to use during installation.
- **Apollo 0.4.6:** Stable, tested, and works best for most users.
- **Sunshine Native:** Experimental, provides better support for newer HID devices and DualSense.


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

**Web UI blank page**
- Clear browser cache and retry
- Check `C:\Program Files\Duo\assets\web\assets\` — should contain `.js` files


**Resolution still low or not updating to my device immediately?**
Here is exactly how the dynamic resolution logic works to bypass the RDP lock:
1. **First Connection:** When you first install or connect, the virtual session launches *before* your device sends its preferred resolution. Thus, it safely falls back to `1920x1080`.
2. **The "Delay":** Once your stream connects, Moonlight's requested resolution (e.g., 4K or 1440p) is logged in the background by Apollo.
3. **Next Connection:** The next time you launch a session, our wrapper instantly reads that logged resolution and starts the virtual monitor precisely matched to your device. 
*If you need to force a change immediately, simply connect, wait 10 seconds, end the stream, and connect again. The new session will adopt your new size.*

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
