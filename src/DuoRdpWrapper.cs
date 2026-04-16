using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

class DuoRdpWrapper {

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

    // Reads target_resolution from C:\Program Files\Duo\config\duo_wrapper.conf
    // Format: target_resolution=1920x1080
    // Allows the user to force a specific resolution regardless of the physical monitor.
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

    // Reads SUNSHINE_CLIENT_WIDTH / SUNSHINE_CLIENT_HEIGHT injected by Sunshine into child processes.
    // Reserved for future compatibility — not currently injected by the Duo fork.
    static bool TryReadSunshineEnvResolution(out int width, out int height) {
        width  = 0;
        height = 0;
        string w = Environment.GetEnvironmentVariable("SUNSHINE_CLIENT_WIDTH");
        string h = Environment.GetEnvironmentVariable("SUNSHINE_CLIENT_HEIGHT");
        if (string.IsNullOrEmpty(w) || string.IsNullOrEmpty(h)) return false;
        return int.TryParse(w, out width) && int.TryParse(h, out height) && width > 0 && height > 0;
    }

    // Finds the active Sunshine/Apollo config file regardless of sunshine_name.
    // Apollo names all config files after sunshine_name (e.g. cosmo.conf, Games.conf).
    // We scan config/*.conf, skip duo_wrapper.conf, and return the first file that
    // contains "log_path" or "min_log_level" — those keys only appear in the main conf.
    static string GetConfPath(string duoDir) {
        string configDir = Path.Combine(duoDir, "config");
        if (!Directory.Exists(configDir)) return null;
        try {
            foreach (string f in Directory.GetFiles(configDir, "*.conf")) {
                string name = Path.GetFileName(f);
                if (name.Equals("duo_wrapper.conf", StringComparison.OrdinalIgnoreCase)) continue;
                try {
                    foreach (string line in File.ReadAllLines(f)) {
                        string t = line.Trim();
                        if (t.StartsWith("log_path", StringComparison.OrdinalIgnoreCase) ||
                            t.StartsWith("min_log_level", StringComparison.OrdinalIgnoreCase))
                            return f;
                    }
                } catch { }
            }
        } catch { }
        // Last resort: fall back to the conventional name
        string fallback = Path.Combine(configDir, "Games.conf");
        return File.Exists(fallback) ? fallback : null;
    }

    // Reads log_path from the active Sunshine/Apollo conf to find the actual log file.
    // Falls back to {conf_stem}.log (e.g. cosmo.log) when the setting is absent.
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
            // Use the same stem as the conf file (e.g. cosmo.conf -> cosmo.log)
            string stem = Path.GetFileNameWithoutExtension(confPath);
            return Path.Combine(duoDir, "config", stem + ".log");
        }
        return Path.Combine(duoDir, "config", "Games.log");
    }

    // Reads the exact resolution requested by Moonlight from the HTTP GET /launch request
    // logged by Sunshine with min_log_level=debug. Reads only the last 512KB to avoid
    // blocking on large log files. Searches from end to start (most recent session).
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
            // Sunshine debug log format: "Debug: mode -- 2560x1600x60"
            // The mode parameter is logged individually after "DESTINATION :: /launch"
            Regex reLaunch = new Regex(@"Debug:\s+mode\s+--\s+(\d+)x(\d+)x\d+",
                RegexOptions.IgnoreCase);
            for (int i = lines.Length - 1; i >= 0; i--) {
                Match m = reLaunch.Match(lines[i]);
                if (m.Success) {
                    int w = int.Parse(m.Groups[1].Value);
                    int h = int.Parse(m.Groups[2].Value);
                    if (w > 0 && h > 0) { width = w; height = h; return true; }
                }
            }
        } catch { }
        return false;
    }

    // Reads the streaming resolution from Games.log (Sunshine/Apollo with min_log_level=info).
    // Sunshine logs "Desktop resolution [WxH]" before invoking DuoRdp.exe.
    // Searches from end to start to get the most recent session.
    static bool TryReadMoonlightResolution(string duoDir, out int width, out int height) {
        width = 0;
        height = 0;
        string logPath = GetLogPath(duoDir);
        if (!File.Exists(logPath)) return false;
        try {
            string[] lines = File.ReadAllLines(logPath);
            Regex reDesktop = new Regex(@"Desktop resolution \[(\d+)x(\d+)\]", RegexOptions.IgnoreCase);
            for (int i = lines.Length - 1; i >= 0; i--) {
                Match m = reDesktop.Match(lines[i]);
                if (m.Success) {
                    int w = int.Parse(m.Groups[1].Value);
                    int h = int.Parse(m.Groups[2].Value);
                    if (w > 0 && h > 0) {
                        width  = w;
                        height = h;
                        return true;
                    }
                }
            }
        } catch { }
        return false;
    }

    // Reads dd_manual_resolution from Apollo's sunshine.conf (secondary fallback).
    // Line format: "dd_manual_resolution = 1920x1080" (with or without spaces).
    static bool TryReadApolloResolution(string duoDir, out int width, out int height) {
        width = 0;
        height = 0;
        string confPath = Path.Combine(duoDir, "config", "sunshine.conf");
        if (!File.Exists(confPath)) return false;
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

    static int Main(string[] args) {
        string log    = @"C:\Users\Public\duordp_args.txt";
        // Derive Duo install directory from the wrapper's own location.
        // DuoRdp.exe (this wrapper) always lives inside the Duo install folder,
        // so this works regardless of where Duo was installed.
        string duoDir = Path.GetDirectoryName(
            Process.GetCurrentProcess().MainModule.FileName);

        using (var sw = new StreamWriter(log, true)) {
            sw.WriteLine("=== DuoRdpWrapper invoked: " + DateTime.Now);
            sw.WriteLine("Args count: " + args.Length);
            for (int i = 0; i < args.Length; i++)
                sw.WriteLine("  [" + i + "] = " + args[i]);
        }

        string[] newArgs = (string[])args.Clone();

        if (args.Length >= 7) {
            int origWidth  = 0;
            int origHeight = 0;
            int.TryParse(args[5], out origWidth);
            int.TryParse(args[6], out origHeight);

            int targetW      = origWidth;
            int targetH      = origHeight;
            string resSource = null;

            // Priority 1: GET /launch mode= from debug log (exact Moonlight-requested resolution)
            // Priority 2: SUNSHINE_CLIENT_WIDTH/HEIGHT env vars (reserved for future compatibility)
            // Priority 3: duo_wrapper.conf (manual override — fallback if log unavailable)
            // Priority 4: Desktop resolution from Games.log (RDP virtual display resolution)
            // Priority 5: dd_manual_resolution from sunshine.conf (Apollo static config)
            // Fallback: use whatever Duo sent
            int rW, rH;
            if (TryReadMoonlightLaunchResolution(duoDir, out rW, out rH)) {
                targetW   = rW;
                targetH   = rH;
                resSource = "Moonlight (GET /launch mode=)";
            } else if (TryReadSunshineEnvResolution(out rW, out rH)) {
                targetW   = rW;
                targetH   = rH;
                resSource = "Sunshine env (SUNSHINE_CLIENT_WIDTH/HEIGHT)";
            } else if (TryReadWrapperConfig(duoDir, out rW, out rH)) {
                targetW   = rW;
                targetH   = rH;
                resSource = "duo_wrapper.conf (custom)";
            } else if (TryReadMoonlightResolution(duoDir, out rW, out rH)) {
                targetW   = rW;
                targetH   = rH;
                resSource = "Games.log (Desktop resolution)";
            } else if (TryReadApolloResolution(duoDir, out rW, out rH)) {
                targetW   = rW;
                targetH   = rH;
                resSource = "Apollo config (dd_manual_resolution)";
            }

            newArgs[5] = targetW.ToString();
            newArgs[6] = targetH.ToString();

            using (var sw = new StreamWriter(log, true)) {
                if (resSource != null && (targetW != origWidth || targetH != origHeight)) {
                    sw.WriteLine("  => Duo sent " + origWidth + "x" + origHeight +
                                 ". Overriding with " + targetW + "x" + targetH +
                                 " [" + resSource + "]");
                } else if (resSource != null) {
                    sw.WriteLine("  => Resolution confirmed: " + targetW + "x" + targetH +
                                 " [" + resSource + "] -- matches what Duo sent.");
                } else {
                    sw.WriteLine("  => Log file not found (" + GetLogPath(duoDir) + "). Using Duo resolution: " +
                                 origWidth + "x" + origHeight);
                }
            }
        }

        string realExe    = Path.Combine(duoDir, "DuoRdp_orig.exe");
        string quotedArgs = string.Join(" ", Array.ConvertAll(newArgs,
            delegate(string a) { return "\"" + a + "\""; }));

        using (var sw = new StreamWriter(log, true)) {
            sw.WriteLine("  => Calling: " + realExe + " " + quotedArgs);
        }

        // Job Object ensures the child process dies when the wrapper exits.
        IntPtr hJob = CreateJobObject(IntPtr.Zero, null);
        if (hJob != IntPtr.Zero) {
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int size   = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(hJob, JobObjectExtendedLimitInformation, ptr, (uint)size);
            Marshal.FreeHGlobal(ptr);
        }

        var psi = new ProcessStartInfo();
        psi.FileName        = realExe;
        psi.Arguments       = quotedArgs;
        psi.UseShellExecute = false;
        var proc = Process.Start(psi);

        if (hJob != IntPtr.Zero)
            AssignProcessToJobObject(hJob, proc.Handle);

        proc.WaitForExit();
        return proc.ExitCode;
    }
}
