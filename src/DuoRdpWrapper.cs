using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

class DuoRdpWrapper {

    // =====================================================================
    // P/Invoke — Job Objects
    // =====================================================================

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    const int JobObjectExtendedLimitInformation = 9;
    const int JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    // =====================================================================
    // P/Invoke — Device Property Management (HID Jailing)
    // =====================================================================

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetAllDevsW(IntPtr classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiSetDevicePropertyW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        ref DEVPROPKEY propertyKey,
        uint propertyType,
        byte[] propertyBuffer,
        uint propertyBufferSize,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiGetDevicePropertyW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        ref DEVPROPKEY propertyKey,
        out uint propertyType,
        byte[] propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize,
        uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID(uint dnDevInst, StringBuilder buffer, uint bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern int CM_Get_DevNode_Registry_Property(uint dnDevInst, uint ulProperty, out uint pulRegDataType, StringBuilder buffer, ref uint pulLength, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Reenumerate_DevNode(uint dnDevInst, uint ulFlags);

    const uint CM_REENUMERATE_SYNCHRONOUS = 0x00000001;
    const uint CM_DRP_SERVICE = 0x00000005;

    // =====================================================================
    // P/Invoke — WTS Session Enumeration
    // =====================================================================

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSEnumerateSessionsW(IntPtr hServer, uint reserved, uint version,
        out IntPtr ppSessionInfo, out uint pCount);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool WTSQuerySessionInformationW(IntPtr hServer, uint sessionId,
        int wtsInfoClass, out IntPtr ppBuffer, out uint pBytesReturned);

    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr pMemory);

    const int WTSUserName = 5;
    const int WTSConnectState = 8;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WTS_SESSION_INFO {
        public uint SessionId;
        public string pWinStationName;
        public int State;
    }

    // Returns the session ID of the active RDP session for the given username.
    // Falls back to -1 if not found. Retries up to maxWait seconds.
    static int GetRdpSessionId(string username, string logPath, int maxWaitSec = 60) {
        DateTime deadline = DateTime.Now.AddSeconds(maxWaitSec);
        while (DateTime.Now < deadline) {
            IntPtr pInfo;
            uint count;
            if (WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out pInfo, out count)) {
                int size = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                for (uint i = 0; i < count; i++) {
                    var info = (WTS_SESSION_INFO)Marshal.PtrToStructure(
                        new IntPtr(pInfo.ToInt64() + i * size), typeof(WTS_SESSION_INFO));
                    // State 0=Active, 4=Disconnected
                    if (info.State != 0 && info.State != 4) continue;
                    IntPtr pUser;
                    uint bytes;
                    if (WTSQuerySessionInformationW(IntPtr.Zero, info.SessionId,
                            WTSUserName, out pUser, out bytes)) {
                        string user = Marshal.PtrToStringUni(pUser);
                        WTSFreeMemory(pUser);
                        if (string.Equals(user, username, StringComparison.OrdinalIgnoreCase)) {
                            WTSFreeMemory(pInfo);
                            Log(logPath, "Jailer: sessao RDP encontrada para usuario '" + username + "' => SessionId=" + info.SessionId);
                            return (int)info.SessionId;
                        }
                    }
                }
                WTSFreeMemory(pInfo);
            }
            System.Threading.Thread.Sleep(2000);
        }
        Log(logPath, "Jailer: sessao RDP para '" + username + "' nao encontrada apos " + maxWaitSec + "s.");
        return -1;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA {
        public uint cbSize;
        public Guid classGuid;
        public uint devInst;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY {
        public Guid fmtid;
        public uint pid;
    }

    static readonly DEVPROPKEY DEVPKEY_Device_SessionId = new DEVPROPKEY {
        fmtid = new Guid("83da6326-97a6-4088-9453-a1923f573b29"),
        pid = 6
    };

    const uint DIGCF_PRESENT = 0x00000002;
    const uint DEVPROP_TYPE_UINT32 = 0x00000007;
    static readonly Guid HID_CLASS_GUID = new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");

    // =====================================================================
    // Configuration & Resolution Logic
    // =====================================================================

    // Reads target_resolution from C:\Program Files\Duo\config\duo_wrapper.conf
    static bool TryReadWrapperConfig(string duoDir, out int width, out int height) {
        width  = 0;
        height = 0;
        string confPath = Path.Combine(duoDir, "config", "duo_wrapper.conf");
        if (!File.Exists(confPath)) return false;
        try {
            foreach (string line in File.ReadAllLines(confPath)) {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("target_resolution", StringComparison.OrdinalIgnoreCase)) continue;
                int eq = trimmed.IndexOf('=');
                if (eq < 0) continue;
                string val = trimmed.Substring(eq + 1).Trim();
                Match m = Regex.Match(val, @"^(\d+)\s*[xX]\s*(\d+)$");
                if (!m.Success) continue;
                width  = int.Parse(m.Groups[1].Value);
                height = int.Parse(m.Groups[2].Value);
                return width > 0 && height > 0;
            }
        } catch { }
        return false;
    }

    // Reads fallback_resolution from duo_wrapper.conf — used when IddCx fails or logs are empty.
    static bool TryReadFallbackResolution(string duoDir, out int width, out int height) {
        width = 0; height = 0;
        string confPath = Path.Combine(duoDir, "config", "duo_wrapper.conf");
        if (!File.Exists(confPath)) return false;
        try {
            foreach (string line in File.ReadAllLines(confPath)) {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("fallback_resolution", StringComparison.OrdinalIgnoreCase)) continue;
                int eq = trimmed.IndexOf('=');
                if (eq < 0) continue;
                string val = trimmed.Substring(eq + 1).Trim();
                Match m = Regex.Match(val, @"^(\d+)\s*[xX]\s*(\d+)$");
                if (!m.Success) continue;
                width  = int.Parse(m.Groups[1].Value);
                height = int.Parse(m.Groups[2].Value);
                return width > 0 && height > 0;
            }
        } catch { }
        return false;
    }

    // Reads last_known_resolution from duo_wrapper.conf — cached from previous successful session.
    static bool TryReadLastKnownResolution(string duoDir, out int width, out int height) {
        width = 0; height = 0;
        string confPath = Path.Combine(duoDir, "config", "duo_wrapper.conf");
        if (!File.Exists(confPath)) return false;
        try {
            foreach (string line in File.ReadAllLines(confPath)) {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("last_known_resolution", StringComparison.OrdinalIgnoreCase)) continue;
                int eq = trimmed.IndexOf('=');
                if (eq < 0) continue;
                string val = trimmed.Substring(eq + 1).Trim();
                Match m = Regex.Match(val, @"^(\d+)\s*[xX]\s*(\d+)$");
                if (!m.Success) continue;
                width  = int.Parse(m.Groups[1].Value);
                height = int.Parse(m.Groups[2].Value);
                return width > 0 && height > 0;
            }
        } catch { }
        return false;
    }

    // Saves last_known_resolution to duo_wrapper.conf for faster reconnections.
    static void SaveLastKnownResolution(string duoDir, int width, int height) {
        string confPath = Path.Combine(duoDir, "config", "duo_wrapper.conf");
        try {
            var lines = new List<string>();
            bool replaced = false;
            if (File.Exists(confPath)) {
                foreach (string line in File.ReadAllLines(confPath)) {
                    if (line.Trim().StartsWith("last_known_resolution", StringComparison.OrdinalIgnoreCase)) {
                        lines.Add("last_known_resolution = " + width + "x" + height);
                        replaced = true;
                    } else {
                        lines.Add(line);
                    }
                }
            }
            if (!replaced) lines.Add("last_known_resolution = " + width + "x" + height);
            File.WriteAllLines(confPath, lines.ToArray());
        } catch { }
    }

    // Removes dd_resolution_option and dd_manual_resolution from the active conf so
    // Sunshine does not lock the virtual display to a stale resolution while we poll.
    // This fixes the v1.0.10 regression where residual 640x480 caused IddCx to lock
    // before the wrapper could detect the real Moonlight resolution.
    static void ClearDisplayResolutionLock(string duoDir) {
        string confPath = GetConfPath(duoDir);
        if (confPath == null || !File.Exists(confPath)) return;
        try {
            string[] lines = File.ReadAllLines(confPath);
            bool changed = false;
            var kept = new List<string>(lines.Length);
            foreach (string line in lines) {
                string t = line.Trim();
                if (t.StartsWith("dd_resolution_option", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("dd_manual_resolution", StringComparison.OrdinalIgnoreCase)) {
                    changed = true;
                    continue;
                }
                kept.Add(line);
            }
            if (changed) File.WriteAllLines(confPath, kept.ToArray());
        } catch { }
    }

    static bool TryReadSunshineEnvResolution(out int width, out int height) {
        width  = 0;
        height = 0;
        string w = Environment.GetEnvironmentVariable("SUNSHINE_CLIENT_WIDTH");
        string h = Environment.GetEnvironmentVariable("SUNSHINE_CLIENT_HEIGHT");
        if (string.IsNullOrEmpty(w) || string.IsNullOrEmpty(h)) return false;
        return int.TryParse(w, out width) && int.TryParse(h, out height) && width > 0 && height > 0;
    }

    static string GetConfPath(string duoDir) {
        string configDir = Path.Combine(duoDir, "config");
        if (!Directory.Exists(configDir)) return null;
        try {
            string bestConf     = null;
            DateTime bestTime   = DateTime.MinValue;
            string fallbackConf = null;

            foreach (string f in Directory.GetFiles(configDir, "*.conf")) {
                string name = Path.GetFileName(f);
                if (name.Equals("duo_wrapper.conf", StringComparison.OrdinalIgnoreCase)) continue;
                try {
                    string logVal = null;
                    foreach (string line in File.ReadAllLines(f)) {
                        string t = line.Trim();
                        if (!t.StartsWith("log_path", StringComparison.OrdinalIgnoreCase)) continue;
                        int eq = t.IndexOf('=');
                        if (eq >= 0) logVal = t.Substring(eq + 1).Trim();
                        break;
                    }
                    if (string.IsNullOrEmpty(logVal)) continue;

                    string logFile = Path.Combine(configDir, logVal);
                    if (File.Exists(logFile)) {
                        DateTime lw = File.GetLastWriteTime(logFile);
                        if (lw > bestTime) { bestTime = lw; bestConf = f; }
                    } else if (fallbackConf == null) {
                        fallbackConf = f;
                    }
                } catch { }
            }
            if (bestConf != null) return bestConf;
            if (fallbackConf != null) return fallbackConf;
        } catch { }
        return null;
    }

    static string GetLogPath(string duoDir) {
        string confPath = GetConfPath(duoDir);
        if (confPath != null && File.Exists(confPath)) {
            try {
                foreach (string line in File.ReadAllLines(confPath)) {
                    string trimmed = line.Trim();
                    if (!trimmed.StartsWith("log_path", StringComparison.OrdinalIgnoreCase)) continue;
                    int eq = trimmed.IndexOf('=');
                    if (eq < 0) continue;
                    string val = trimmed.Substring(eq + 1).Trim();
                    if (!string.IsNullOrEmpty(val))
                        return Path.Combine(duoDir, "config", val);
                }
            } catch { }
            string stem = Path.GetFileNameWithoutExtension(confPath);
            return Path.Combine(duoDir, "config", stem + ".log");
        }
        return Path.Combine(duoDir, "config", "Games.log");
    }

    static bool SetDisplayResolution(string duoDir, int width, int height) {
        string confPath = GetConfPath(duoDir);
        if (confPath == null || !File.Exists(confPath)) return false;
        try {
            string[] lines = File.ReadAllLines(confPath);
            bool hadOption = false, hadManual = false;
            var result = new List<string>(lines.Length + 2);
            foreach (string line in lines) {
                string t = line.Trim();
                if (t.StartsWith("dd_resolution_option", StringComparison.OrdinalIgnoreCase)) {
                    result.Add(width > 0 ? "dd_resolution_option = manual" : "dd_resolution_option = disabled");
                    hadOption = true;
                    continue;
                }
                if (t.StartsWith("dd_manual_resolution", StringComparison.OrdinalIgnoreCase)) {
                    if (width > 0) { result.Add("dd_manual_resolution = " + width + "x" + height); hadManual = true; }
                    continue;
                }
                result.Add(line);
            }
            if (!hadOption)
                result.Add(width > 0 ? "dd_resolution_option = manual" : "dd_resolution_option = disabled");
            if (!hadManual && width > 0)
                result.Add("dd_manual_resolution = " + width + "x" + height);

            string[] newLines = result.ToArray();
            if (newLines.Length != lines.Length) { File.WriteAllLines(confPath, newLines); return true; }
            for (int i = 0; i < newLines.Length; i++) {
                if (newLines[i] != lines[i]) { File.WriteAllLines(confPath, newLines); return true; }
            }
            return false;
        } catch { return false; }
    }

    static string ReadSunshineName(string duoDir) {
        string confPath = GetConfPath(duoDir);
        if (confPath == null) return null;
        try {
            foreach (string line in File.ReadAllLines(confPath)) {
                string t = line.Trim();
                if (!t.StartsWith("sunshine_name", StringComparison.OrdinalIgnoreCase)) continue;
                int eq = t.IndexOf('=');
                if (eq < 0) continue;
                string val = t.Substring(eq + 1).Trim();
                if (!string.IsNullOrEmpty(val)) return val;
            }
        } catch { }
        return null;
    }

    static bool TryReadMoonlightLaunchResolution(string duoDir, out int width, out int height) {
        width  = 0;
        height = 0;
        string logPath = GetLogPath(duoDir);
        if (!File.Exists(logPath)) return false;
        try {
            const int maxBytes = 512 * 1024;
            string content;
            using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                long start = Math.Max(0, fs.Length - maxBytes);
                fs.Seek(start, SeekOrigin.Begin);
                using (var sr = new StreamReader(fs))
                    content = sr.ReadToEnd();
            }
            string[] lines = content.Split('\n');
            Regex reLaunch = new Regex(
                @"^\[(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})\.\d+\].*Debug:\s+mode\s+--\s+(\d+)x(\d+)x\d+",
                RegexOptions.IgnoreCase);
            DateTime now = DateTime.Now;
            for (int i = lines.Length - 1; i >= 0; i--) {
                Match m = reLaunch.Match(lines[i]);
                if (!m.Success) continue;
                DateTime ts;
                if (DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out ts) &&
                    (now - ts).TotalSeconds > 60) break;
                int w = int.Parse(m.Groups[2].Value);
                int h = int.Parse(m.Groups[3].Value);
                if (w > 0 && h > 0) { width = w; height = h; return true; }
            }
        } catch { }
        return false;
    }

    static bool TryReadMoonlightResolution(string duoDir, out int width, out int height) {
        width = 0;
        height = 0;
        string logPath = GetLogPath(duoDir);
        if (!File.Exists(logPath)) return false;
        try {
            const int maxBytes = 512 * 1024;
            string content;
            using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                long start = Math.Max(0, fs.Length - maxBytes);
                fs.Seek(start, SeekOrigin.Begin);
                using (var sr = new StreamReader(fs))
                    content = sr.ReadToEnd();
            }
            string[] lines = content.Split('\n');
            Regex reDesktop = new Regex(@"Desktop resolution \[(\d+)x(\d+)\]", RegexOptions.IgnoreCase);
            for (int i = lines.Length - 1; i >= 0; i--) {
                Match m = reDesktop.Match(lines[i]);
                if (m.Success) {
                    int w = int.Parse(m.Groups[1].Value);
                    int h = int.Parse(m.Groups[2].Value);
                    if (w >= 800 && h >= 600) {
                        width  = w;
                        height = h;
                        return true;
                    }
                }
            }
        } catch { }
        return false;
    }

    static bool TryReadApolloResolution(string duoDir, out int width, out int height) {
        width = 0;
        height = 0;
        string confPath = GetConfPath(duoDir);
        if (confPath == null || !File.Exists(confPath)) return false;
        try {
            foreach (string line in File.ReadAllLines(confPath)) {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("dd_manual_resolution", StringComparison.OrdinalIgnoreCase))
                    continue;
                int eq = trimmed.IndexOf('=');
                if (eq < 0) continue;
                string val = trimmed.Substring(eq + 1).Trim();
                Match m = Regex.Match(val, @"^(\d+)\s*[xX]\s*(\d+)$");
                if (!m.Success) continue;
                width  = int.Parse(m.Groups[1].Value);
                height = int.Parse(m.Groups[2].Value);
                return width > 0 && height > 0;
            }
        } catch { }
        return false;
    }

    // =====================================================================
    // HID Jailing Logic (Multiseat Isolation)
    // =====================================================================

    static void StartGamepadJailer(string wrapperLog, string rdpUsername) {
        int mySessionId = Process.GetCurrentProcess().SessionId;
        Log(wrapperLog, "Jailer: thread iniciada. ProcessSessionId=" + mySessionId + " usuario=" + rdpUsername);
        var jailerThread = new System.Threading.Thread(() => {
            int rdpSessionId = GetRdpSessionId(rdpUsername, wrapperLog, 60);
            if (rdpSessionId < 0) return;
            var seenDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int cycle = 0;
            while (true) {
                try {
                    JailGamepads(rdpSessionId, seenDevices, wrapperLog, cycle++);
                } catch (Exception ex) {
                    Log(wrapperLog, "Jailer: excecao no ciclo " + cycle + ": " + ex.Message);
                }
                System.Threading.Thread.Sleep(2000);
            }
        });
        jailerThread.IsBackground = true;
        jailerThread.Name = "DuoGamepadJailer";
        jailerThread.Start();
    }

    const uint DIGCF_ALLCLASSES = 0x00000004;

    static void JailGamepads(int sessionId, HashSet<string> seen, string logPath, int cycle) {
        IntPtr devs = SetupDiGetAllDevsW(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (devs == new IntPtr(-1)) {
            if (cycle == 0) Log(logPath, "Jailer: SetupDiGetAllDevsW falhou. Erro=" + Marshal.GetLastWin32Error());
            return;
        }

        try {
            uint idx = 0;
            uint total = 0;
            while (true) {
                var devData = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                if (!SetupDiEnumDeviceInfo(devs, idx, ref devData)) break;
                idx++;
                total++;

                var sb = new StringBuilder(512);
                if (CM_Get_Device_ID(devData.devInst, sb, 512, 0) != 0) continue;
                string instanceId = sb.ToString();

                bool isXinput = instanceId.IndexOf("IG_", StringComparison.OrdinalIgnoreCase) >= 0
                    || instanceId.IndexOf("VID_045E", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!IsViGEmDevice(devData.devInst)) continue;

                // Check current SessionId — re-jail if missing or wrong (e.g. after Home button re-enumeration)
                DEVPROPKEY key = DEVPKEY_Device_SessionId;
                byte[] curBuf = new byte[4]; uint pt2; uint reqSz;
                bool alreadyJailed = SetupDiGetDevicePropertyW(devs, ref devData, ref key, out pt2, curBuf, 4, out reqSz, 0)
                    && BitConverter.ToUInt32(curBuf, 0) == (uint)sessionId;

                if (alreadyJailed) { seen.Add(instanceId); continue; }

                if (!seen.Contains(instanceId))
                    if (isXinput) LogDeviceAncestors(devData.devInst, instanceId, logPath);

                Log(logPath, "Jailer: ViGEm device detected: " + instanceId);
                byte[] sidBuf = BitConverter.GetBytes((uint)sessionId);
                if (SetupDiSetDevicePropertyW(devs, ref devData, ref key, DEVPROP_TYPE_UINT32, sidBuf, 4, 0)) {
                    CM_Reenumerate_DevNode(devData.devInst, CM_REENUMERATE_SYNCHRONOUS);
                    Log(logPath, "  => Jailed into session " + sessionId + " successfully.");
                    seen.Add(instanceId);
                } else {
                    int err = Marshal.GetLastWin32Error();
                    Log(logPath, "  !! Jailing failed. Win32Error=" + err);
                }
            }
            // Log diagnóstico apenas no primeiro ciclo
            if (cycle == 0) Log(logPath, "Jailer: ciclo 0 concluido. HID devices encontrados=" + total);
        } finally {
            SetupDiDestroyDeviceInfoList(devs);
        }
    }

    static void LogDeviceAncestors(uint devInst, string instanceId, string logPath) {
        var sb2 = new StringBuilder(512);
        uint cur = devInst;
        var chain = new System.Text.StringBuilder();
        for (int lvl = 0; lvl < 4; lvl++) {
            uint parent;
            if (CM_Get_Parent(out parent, cur, 0) != 0) break;
            uint pt; uint bs = 512;
            var sb = new StringBuilder((int)bs);
            string svc = CM_Get_DevNode_Registry_Property(parent, CM_DRP_SERVICE, out pt, sb, ref bs, 0) == 0
                ? sb.ToString() : "(none)";
            if (CM_Get_Device_ID(parent, sb2, 512, 0) == 0)
                chain.Append(" L" + lvl + "=[" + svc + "]");
            else
                chain.Append(" L" + lvl + "=[" + svc + "]");
            cur = parent;
        }
        if (instanceId.IndexOf("IG_", StringComparison.OrdinalIgnoreCase) >= 0 ||
            instanceId.IndexOf("VID_045E", StringComparison.OrdinalIgnoreCase) >= 0)
            Log(logPath, "  DIAG: " + instanceId + chain);
    }

    static bool IsViGEmDevice(uint devInst) {
        uint cur = devInst;
        for (int level = 0; level < 4; level++) {
            uint parent;
            if (CM_Get_Parent(out parent, cur, 0) != 0) return false;

            uint propType;
            uint bufferSize = 512;
            var sb = new StringBuilder((int)bufferSize);
            if (CM_Get_DevNode_Registry_Property(parent, CM_DRP_SERVICE, out propType, sb, ref bufferSize, 0) == 0) {
                string svc = sb.ToString();
                if ("xusb22".Equals(svc, StringComparison.OrdinalIgnoreCase) ||
                    "ds4drv".Equals(svc, StringComparison.OrdinalIgnoreCase) ||
                    "ViGEmBus".Equals(svc, StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }
            cur = parent;
        }
        return false;
    }

    // =====================================================================
    // Main Entry Point
    // =====================================================================

    static string BuildQuotedArgs(string[] a) {
        return string.Join(" ", Array.ConvertAll(a,
            delegate(string s) { return "\"" + s + "\""; }));
    }

    static void Log(string logFile, string msg) {
        try {
            File.AppendAllText(logFile, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + msg + Environment.NewLine);
        } catch { }
    }

    // Mantém apenas linhas dos últimos N dias no log do wrapper.
    static void TruncateWrapperLog(string logFile, int days = 2) {
        try {
            if (!File.Exists(logFile)) return;
            string[] lines = File.ReadAllLines(logFile);
            if (lines.Length == 0) return;
            DateTime cutoff = DateTime.Now.AddDays(-days);
            var keep = new List<string>();
            foreach (var line in lines) {
                if (line.Length < 24) { keep.Add(line); continue; }
                if (line[0] != '[') { keep.Add(line); continue; }
                DateTime dt;
                if (DateTime.TryParseExact(line.Substring(1, 19), "yyyy-MM-dd HH:mm:ss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out dt)) {
                    if (dt >= cutoff) keep.Add(line);
                } else {
                    keep.Add(line);
                }
            }
            if (keep.Count < lines.Length)
                File.WriteAllLines(logFile, keep);
        } catch { }
    }

    static int Main(string[] args) {
        string logPath = @"C:\Users\Public\duordp_args.txt";
        string duoDir  = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);

        // Limpa logs antigos do wrapper (mantem 2 dias)
        TruncateWrapperLog(logPath, 2);

        // Single instance lock per user to prevent double login sessions
        bool createdNew;
        string mutexName = "Global\\DuoRdpWrapper_" + Environment.UserName;
        using (var mutex = new System.Threading.Mutex(true, mutexName, out createdNew)) {
            if (!createdNew) {
                Log(logPath, "!!! DuoRdpWrapper already running for user " + Environment.UserName + ". Exiting to prevent double session.");
                return 0;
            }

            Log(logPath, "=== DuoRdpWrapper invoked: " + DateTime.Now);
            Log(logPath, "Args count: " + args.Length);
            for (int i = 0; i < args.Length; i++)
                Log(logPath, "  [" + i + "] = " + args[i]);

            // CRITICAL FIX: Remove stale dd_manual_resolution before any polling.
            // In v1.0.10 this cleanup was removed, causing IddCx to lock at 640x480
            // while the wrapper waited for Sunshine logs. This restores v1.0.9 behavior.
            ClearDisplayResolutionLock(duoDir);
            Log(logPath, "  => ClearDisplayResolutionLock executed (stale 640x480 removed if present).");

            // Start the HID Jailer thread to isolate controllers created by official Sunshine
            // args[2] = username in DuoManagerService mode; fallback to Environment.UserName
            string rdpUser = (args.Length >= 3 && !string.IsNullOrEmpty(args[2]))
                ? args[2] : Environment.UserName;
            StartGamepadJailer(logPath, rdpUser);

        string[] newArgs;
        int currentW = 0, currentH = 0;

        if (args.Length == 0) {
            // ── Sunshine direct mode ──────────────────────────────────────────
            int w = 0, h = 0;
            string resSource = null;

            int rW, rH;
            if (TryReadSunshineEnvResolution(out rW, out rH)) {
                w = rW; h = rH; resSource = "SUNSHINE_CLIENT_WIDTH/HEIGHT (Apollo env)";
            } else if (TryReadMoonlightLaunchResolution(duoDir, out rW, out rH)) {
                w = rW; h = rH; resSource = "Moonlight (GET /launch mode= from log)";
            } else if (TryReadMoonlightResolution(duoDir, out rW, out rH)) {
                w = rW; h = rH; resSource = "log (Desktop resolution)";
            } else if (TryReadWrapperConfig(duoDir, out rW, out rH)) {
                w = rW; h = rH; resSource = "duo_wrapper.conf";
            } else if (TryReadApolloResolution(duoDir, out rW, out rH)) {
                w = rW; h = rH; resSource = "Apollo config (dd_manual_resolution)";
            }

            if (w <= 0 || h <= 0) { w = 1920; h = 1080; resSource = "fallback 1920x1080"; }

            string machineName = ReadSunshineName(duoDir) ?? Environment.MachineName;
            string userName    = Environment.UserName;
            string domainName  = Environment.UserDomainName;
            int    lcid        = CultureInfo.CurrentCulture.LCID;

            newArgs = new string[] { "127.0.0.1", machineName, userName, domainName, lcid.ToString(), w.ToString(), h.ToString() };
            currentW = w; currentH = h;
            Log(logPath, "  => Sunshine direct mode. machine=" + machineName + " user=" + userName + "@" + domainName + " res=" + w + "x" + h + " [" + resSource + "]");
        } else {
            // ── DuoManagerService mode ────────────────────────────────────────
            newArgs = (string[])args.Clone();
            if (args.Length >= 7) {
                int origWidth, origHeight;
                int.TryParse(args[5], out origWidth);
                int.TryParse(args[6], out origHeight);

                int targetW = origWidth, targetH = origHeight;
                string resSource = null;

                int rW, rH;
                // Sunshine may take several minutes to write the first log entry on initial
                // connection (observed: up to 4 minutes). We poll for 180s before falling back.
                DateTime waitUntil = DateTime.Now.AddSeconds(180);
                while (!TryReadMoonlightLaunchResolution(duoDir, out rW, out rH)) {
                    if (DateTime.Now >= waitUntil) break;
                    System.Threading.Thread.Sleep(300);
                }
                if (rW > 0 && rH > 0) { targetW = rW; targetH = rH; resSource = "Moonlight (GET /launch mode=)"; }
                else if (TryReadSunshineEnvResolution(out rW, out rH)) { targetW = rW; targetH = rH; resSource = "Sunshine env"; }
                else if (TryReadWrapperConfig(duoDir, out rW, out rH)) { targetW = rW; targetH = rH; resSource = "duo_wrapper.conf"; }
                else if (TryReadMoonlightResolution(duoDir, out rW, out rH)) { targetW = rW; targetH = rH; resSource = "Games.log"; }
                else if (TryReadApolloResolution(duoDir, out rW, out rH)) { targetW = rW; targetH = rH; resSource = "Apollo config"; }
                else if (TryReadLastKnownResolution(duoDir, out rW, out rH)) { targetW = rW; targetH = rH; resSource = "last_known_resolution"; }
                else if (TryReadFallbackResolution(duoDir, out rW, out rH)) { targetW = rW; targetH = rH; resSource = "fallback_resolution"; }

                SetDisplayResolution(duoDir, targetW, targetH);
                newArgs[5] = targetW.ToString();
                newArgs[6] = targetH.ToString();
                currentW = targetW; currentH = targetH;

                // Cache successful resolution for next session
                if (targetW > 0 && targetH > 0 && targetW != 640 && targetH != 480) {
                    SaveLastKnownResolution(duoDir, targetW, targetH);
                }

                Log(logPath, "  => Resolution resolved: " + targetW + "x" + targetH + " [" + (resSource ?? "Duo default") + "]");
            }
        }

        string realExe = Path.Combine(duoDir, "DuoRdp_orig.exe");

        // Job Object for cleanup
        IntPtr hJob = CreateJobObject(IntPtr.Zero, null);
        if (hJob != IntPtr.Zero) {
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(hJob, JobObjectExtendedLimitInformation, ptr, (uint)size);
            Marshal.FreeHGlobal(ptr);
        }

        var psi = new ProcessStartInfo(realExe);
        psi.UseShellExecute = false;

        while (true) {
            psi.Arguments = BuildQuotedArgs(newArgs);
            Log(logPath, "  => Calling: " + realExe + " " + psi.Arguments);

            var proc = Process.Start(psi);
            if (hJob != IntPtr.Zero) AssignProcessToJobObject(hJob, proc.Handle);

            bool resolutionChanged = false;
            while (!proc.WaitForExit(5000)) {
                int rW, rH;
                if (TryReadMoonlightLaunchResolution(duoDir, out rW, out rH) && (rW != currentW || rH != currentH)) {
                    Log(logPath, "=== Resolution change detected: " + currentW + "x" + currentH + " -> " + rW + "x" + rH);
                    SetDisplayResolution(duoDir, rW, rH);
                    try { proc.Kill(); } catch { }
                    proc.WaitForExit();
                    currentW = rW; currentH = rH;
                    newArgs[newArgs.Length - 2] = rW.ToString();
                    newArgs[newArgs.Length - 1] = rH.ToString();
                    resolutionChanged = true;
                    break;
                }
            }
            if (!resolutionChanged) return proc.ExitCode;
        }
    }
}
}
