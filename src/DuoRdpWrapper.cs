using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

class DuoRdpWrapper {

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSEnumerateSessionsW(IntPtr hServer, uint reserved, uint version, out IntPtr ppSessionInfo, out uint pCount);
    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool WTSQuerySessionInformationW(IntPtr hServer, uint sessionId, int wtsInfoClass, out IntPtr ppBuffer, out uint pBytesReturned);
    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr pMemory);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSLogoffSession(IntPtr hServer, uint sessionId, bool bWait);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WTS_SESSION_INFO { public uint SessionId; public string pWinStationName; public int State; }

    static void Log(string logFile, string msg) {
        try { File.AppendAllText(logFile, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + msg + Environment.NewLine); } catch { }
    }

    static int RoundWidthForRdp(int w) { return (w + 3) & ~3; }

    static void ForceLogoffUser(string username, string logPath) {
        if (string.IsNullOrEmpty(username) || username.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)) return;
        try {
            IntPtr pInfo; uint count;
            if (WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out pInfo, out count)) {
                int size = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                for (uint i = 0; i < count; i++) {
                    var info = (WTS_SESSION_INFO)Marshal.PtrToStructure(new IntPtr(pInfo.ToInt64() + i * size), typeof(WTS_SESSION_INFO));
                    IntPtr pUser; uint bytes;
                    if (WTSQuerySessionInformationW(IntPtr.Zero, info.SessionId, 5, out pUser, out bytes)) {
                        string user = Marshal.PtrToStringUni(pUser); WTSFreeMemory(pUser);
                        if (string.Equals(user, username, StringComparison.OrdinalIgnoreCase)) {
                            Log(logPath, "Finalizador: Logoff sessao " + info.SessionId + " (" + username + ")");
                            try { Process.Start(new ProcessStartInfo("logoff.exe", info.SessionId.ToString()){ CreateNoWindow=true, UseShellExecute=false }).WaitForExit(1000); } catch{}
                            WTSLogoffSession(IntPtr.Zero, info.SessionId, false);
                        }
                    }
                }
                WTSFreeMemory(pInfo);
            }
        } catch (Exception ex) { Log(logPath, "Logoff Error: " + ex.Message); }
    }

    static string GetConfPath(string duoDir) {
        string configDir = Path.Combine(duoDir, "config");
        if (!Directory.Exists(configDir)) return null;
        try {
            string bestConf = null; DateTime bestTime = DateTime.MinValue;
            foreach (string f in Directory.GetFiles(configDir, "*.conf")) {
                if (Path.GetFileName(f).Equals("duo_wrapper.conf", StringComparison.OrdinalIgnoreCase)) continue;
                if (File.GetLastWriteTime(f) > bestTime) { bestTime = File.GetLastWriteTime(f); bestConf = f; }
            }
            return bestConf;
        } catch { return null; }
    }

    static string GetLogPath(string duoDir) {
        string conf = GetConfPath(duoDir);
        if (conf != null) {
            try {
                foreach (string line in File.ReadAllLines(conf)) {
                    if (line.Trim().StartsWith("log_path", StringComparison.OrdinalIgnoreCase)) {
                        int eq = line.IndexOf('='); if (eq >= 0) return Path.Combine(duoDir, "config", line.Substring(eq + 1).Trim());
                    }
                }
            } catch { }
        }
        return Path.Combine(duoDir, "config", "sunshine.log");
    }

    static bool TryReadMoonlightLaunchResolution(string duoDir, out int width, out int height) {
        width = 0; height = 0; string logPath = GetLogPath(duoDir);
        if (!File.Exists(logPath)) return false;
        try {
            using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                fs.Seek(Math.Max(0, fs.Length - 102400), SeekOrigin.Begin);
                using (var sr = new StreamReader(fs)) {
                    string content = sr.ReadToEnd();
                    Match m = Regex.Match(content, @"mode\s+--\s+(\d+)x(\d+)x\d+", RegexOptions.RightToLeft | RegexOptions.IgnoreCase);
                    if (m.Success) { width = int.Parse(m.Groups[1].Value); height = int.Parse(m.Groups[2].Value); return true; }
                }
            }
        } catch { }
        return false;
    }

    static void SaveLastKnownResolution(string duoDir, int w, int h) {
        try { File.WriteAllText(Path.Combine(duoDir, "config", "duo_wrapper.conf"), "last_known_resolution = " + w + "x" + h); } catch { }
    }

    static bool TryReadLastKnownResolution(string duoDir, out int w, out int h) {
        w = 0; h = 0; string p = Path.Combine(duoDir, "config", "duo_wrapper.conf");
        if (!File.Exists(p)) return false;
        try {
            string c = File.ReadAllText(p);
            Match m = Regex.Match(c, @"last_known_resolution\s*=\s*(\d+)x(\d+)");
            if (m.Success) { w = int.Parse(m.Groups[1].Value); h = int.Parse(m.Groups[2].Value); return true; }
        } catch { }
        return false;
    }

    static bool SetDisplayResolution(string duoDir, int w, int h) {
        string p = GetConfPath(duoDir); if (p == null) return false;
        try {
            List<string> lines = new List<string>(File.ReadAllLines(p));
            lines.RemoveAll(l => l.Trim().StartsWith("dd_resolution_option") || l.Trim().StartsWith("dd_manual_resolution"));
            lines.Add("dd_resolution_option = manual");
            lines.Add("dd_manual_resolution = " + w + "x" + h);
            File.WriteAllLines(p, lines.ToArray()); return true;
        } catch { return false; }
    }

    static string _currentRdpUser = null;
    static string _currentLogPath = null;
    static bool _rdpFinishedCleanly = false;

    static int Main(string[] args) {
        _currentLogPath = @"C:\Users\Public\duordp_args.txt";
        string duoDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
        _currentRdpUser = (args.Length >= 3) ? args[2] : Environment.UserName;

        AppDomain.CurrentDomain.ProcessExit += (s, e) => { if (!_rdpFinishedCleanly) ForceLogoffUser(_currentRdpUser, _currentLogPath); };

        Log(_currentLogPath, "=== DuoRdpWrapper invoked: " + DateTime.Now);
        
        string[] newArgs = (args.Length > 0) ? (string[])args.Clone() : new string[0];
        int currentW = 0, currentH = 0;

        if (args.Length >= 7) {
            int rW = 0, rH = 0;
            DateTime limit = DateTime.Now.AddSeconds(15);
            while (DateTime.Now < limit && !TryReadMoonlightLaunchResolution(duoDir, out rW, out rH)) System.Threading.Thread.Sleep(500);

            if (rW == 0) TryReadLastKnownResolution(duoDir, out rW, out rH);
            if (rW == 0) { rW = 1920; rH = 1080; }

            int lW, lH;
            if (TryReadLastKnownResolution(duoDir, out lW, out lH) && (rW != lW || rH != lH)) {
                Log(_currentLogPath, "SmartSync: Mudando para " + rW + "x" + rH);
                ForceLogoffUser(_currentRdpUser, _currentLogPath); System.Threading.Thread.Sleep(2000);
            }
            SetDisplayResolution(duoDir, rW, rH);
            SaveLastKnownResolution(duoDir, rW, rH);
            newArgs[5] = RoundWidthForRdp(rW).ToString(); newArgs[6] = rH.ToString();
            currentW = rW; currentH = rH;
        }

        string realExe = Path.Combine(duoDir, "DuoRdp_orig.exe");
        var psi = new ProcessStartInfo(realExe) { UseShellExecute = false };
        while (true) {
            psi.Arguments = string.Join(" ", Array.ConvertAll(newArgs, s => "\"" + s + "\""));
            Log(_currentLogPath, "  => Calling: " + realExe + " " + psi.Arguments);
            var proc = Process.Start(psi);
            bool resChanged = false;
            while (!proc.WaitForExit(3000)) {
                int rW, rH;
                if (TryReadMoonlightLaunchResolution(duoDir, out rW, out rH) && (rW != currentW || rH != currentH)) {
                    Log(_currentLogPath, "=== Res change: " + rW + "x" + rH);
                    SaveLastKnownResolution(duoDir, rW, rH); SetDisplayResolution(duoDir, rW, rH);
                    try { if (!proc.CloseMainWindow()) proc.Kill(); } catch { }
                    proc.WaitForExit(); ForceLogoffUser(_currentRdpUser, _currentLogPath); System.Threading.Thread.Sleep(2000);
                    newArgs[5] = RoundWidthForRdp(rW).ToString(); newArgs[6] = rH.ToString();
                    currentW = rW; currentH = rH; resChanged = true; break;
                }
            }
            if (!resChanged) { _rdpFinishedCleanly = true; return proc.ExitCode; }
        }
    }
}
