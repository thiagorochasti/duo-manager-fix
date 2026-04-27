# Duo Manager Fix

The ultimate community patch for **[Duo Manager 1.5.6](https://github.com/DuoStream/Duo/releases/tag/v1.5.6)**. This fix resolves critical issues that appear on recent Windows 11 builds with NVIDIA RTX GPUs and [Moonlight](https://moonlight-stream.org/).

## 🌟 What's New in v1.0.11-beta

This version introduces **Smart Resolution Sync** and **Proactive Gamepad Isolation**, making Duo Manager stable for competitive and multi-user environments.

### 🛡️ Kernel-Level Gamepad Isolation
- **No Steam Bleeding:** The controller is hidden at the Kernel level using DACL (Security Descriptors) and HidHide Filters before it's even created.
- **Console Logon Protection (SID S-1-2-1):** Blocks any process running on the physical monitor (like Steam) from seeing the virtual controller while allowing full access for the remote session.

### 🖥️ Smart Resolution & Persistence
- **Crystal Clear Image:** Automatic rounding to multiple-of-4 width (e.g., 1366 becomes 1368) to satisfy the RDP protocol and prevent blur.
- **Smart Sync:** 
  - **Same resolution:** Instant reconnect, game continues.
  - **Different resolution:** Automatic logoff and physical monitor resize (takes ~2 seconds).
- **Why logoff?** Due to Windows IddCx driver limitations, a virtual monitor can only physically change its dimensions when the user session is re-initialized.

### 🧹 Automatic Cleanup
- **No Ghost Sessions:** Native `logoff.exe` is called when the service stops or resolution changes, ensuring no disconnected "Games" users are left in memory.
- **Fast Startup:** Reduced log polling timeout from 180s to 15s.

---

## 🚀 Installation

1. Download the latest release: `release/DuoManagerFix-Setup.exe`.
2. Run the installer as Administrator.
3. **Reboot your computer** (Required to reset the Virtual Display Driver).
4. Connect via Moonlight and enjoy!

---

## 🛠️ Development & Contributions

This project is open-source. If you find a way to dynamically resize the `IddCx` virtual monitor without a full logoff, feel free to contribute!

### Build from source:
1. Clone the repo: `git clone https://github.com/thiagorochasti/duo-manager-fix.git`
2. Run `scripts\build.bat` (Requires .NET 4.x).
3. Ensure Apollo/Sunshine binaries are in `bundled/`.
4. Open `installer\setup.iss` in **Inno Setup 6** and compile (F9).

---

## License
MIT
