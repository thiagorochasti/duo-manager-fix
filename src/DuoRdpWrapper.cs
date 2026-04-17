using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    // Present when Apollo/Sunshine calls DuoRdp.exe directly as the app do_cmd.
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
        // Always prefer Games.conf — Duo may create per-session conf files (e.g. Guest.conf)
        // that share the same keys but point to the wrong log file.
        string gamesConf = Path.Combine(configDir, "Games.conf");
        if (File.Exists(gamesConf)) return gamesConf;
            // Fallback: scan for another conf with log_path (e.g. cosmo.conf).
        // We require log_path specifically — per-session confs (Guest.conf, etc.) share
        // min_log_level but never declare log_path, so this avoids picking the wrong file.
        try {
            string minLogCandidate = null;
            foreach (string f in Directory.GetFiles(configDir, "*.conf")) {
                string name = Path.GetFileName(f);
                if (name.Equals("duo_wrapper.conf", StringComparison.OrdinalIgnoreCase)) continue;
                try {
                    bool hasLogPath   = false;
                    bool hasMinLog    = false;
                    foreach (string line in File.ReadAllLines(f)) {
                        string t = line.Trim();
                        if (t.StartsWith("log_path", StringComparison.OrdinalIgnoreCase))    hasLogPath = true;
                        if (t.StartsWith("min_log_level", StringComparison.OrdinalIgnoreCase)) hasMinLog = true;
                    }
                    if (hasLogPath) return f;                    // main conf: has log_path
                    if (hasMinLog && minLogCandidate == null) minLogCandidate = f; // last resort
                } catch { }
            }
            if (minLogCandidate != null) return minLogCandidate;
        } catch { }
        return null;
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

    // Removes dd_resolution_option and dd_manual_resolution from the active conf so
    // Sunshine does not lock the virtual display to the host monitor resolution.
    // Duo writes these on every guest connect based on the physical monitor; without
    // this cleanup, Sunshine overrides the display to e.g. 2560x1440 regardless of
    // what Moonlight requested, and the RDP session resolution has no effect.
    static void ClearDisplayResolutionLock(string duoDir) {
        string confPath = GetConfPath(duoDir);
        if (confPath == null || !File.Exists(confPath)) return;
        try {
            string[] lines = File.ReadAllLines(confPath);
            bool changed = false;
            var kept = new System.Collections.Generic.List<string>(lines.Length);
            foreach (string line in lines) {
                string t = line.Trim();
                if (t.StartsWith("dd_resolution_option", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("dd_manual_resolution", StringComparison.OrdinalIgnoreCase)) {
                    changed = true;
                    continue;
                }
                kept.Add(line);
            }
            if (changed)
                File.WriteAllLines(confPath, kept.ToArray());
        } catch { }
    }

    // Reads sunshine_name from the active Sunshine/Apollo conf.
    // Falls back to null when the setting is absent (caller should use Environment.MachineName).
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

    // Reads the exact resolution requested by Moonlight from the HTTP GET /launch request
    // logged by Sunshine with min_log_level=debug. Reads only the last 512KB to avoid
    // blocking on large log files. Searches from end to start (most recent session).
    // Only accepts entries whose timestamp is within 60 seconds of now, to avoid using
    // stale entries from a previous Moonlight session when a new connection is starting.
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
            // Sunshine debug log format: "[2026-04-16 20:40:30.581]: Debug: mode -- 2560x1600x60"
            // The mode parameter is logged individually after "DESTINATION :: /launch"
            Regex reLaunch = new Regex(
                @"^\[(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})\.\d+\].*Debug:\s+mode\s+--\s+(\d+)x(\d+)x\d+",
                RegexOptions.IgnoreCase);
            DateTime now = DateTime.Now;
            for (int i = lines.Length - 1; i >= 0; i--) {
                Match m = reLaunch.Match(lines[i]);
                if (!m.Success) continue;
                // Reject entries older than 60 seconds — they belong to a previous session
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

    // Reads the streaming resolution from Games.log (Sunshine/Apollo with min_log_level=info).
    // Sunshine logs "Desktop resolution [WxH]" before invoking DuoRdp.exe.
    // Searches from end to start to get the most recent session. Reads only the last 512KB.
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

    // Reads dd_manual_resolution from the active Apollo/Sunshine conf (secondary fallback).
    // Uses GetConfPath() so it finds Games.conf (or whatever the active conf is named)
    // instead of relying on the hardcoded "sunshine.conf" name which Duo does not use.
    // Line format: "dd_manual_resolution = 1920x1080" (with or without spaces).
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

    static string BuildQuotedArgs(string[] a) {
        return string.Join(" ", Array.ConvertAll(a,
            delegate(string s) { return "\"" + s + "\""; }));
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

        // Remove dd_resolution_option/dd_manual_resolution so Sunshine does not lock
        // the virtual display to the host monitor resolution before the RDP session starts.
        ClearDisplayResolutionLock(duoDir);

        string[] newArgs;
        int currentW = 0, currentH = 0;

        if (args.Length == 0) {
            // ── Sunshine direct mode ──────────────────────────────────────────────────
            // Called by Apollo/Sunshine as the app do_cmd (no args).
            // SUNSHINE_CLIENT_* env vars are injected per session — use them as
            // the primary resolution source.
            int w = 0, h = 0;
            string resSource = null;

            // Priority 1: env vars injected by Apollo per session (most reliable)
            // Priority 2: debug log mode= entry (within 60 s)
            // Priority 3: info log Desktop resolution
            // Priority 4: duo_wrapper.conf manual override
            // Priority 5: Apollo dd_manual_resolution
            // Fallback : 1920x1080
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

            newArgs = new string[] {
                "127.0.0.1",
                machineName,
                userName,
                domainName,
                lcid.ToString(),
                w.ToString(),
                h.ToString()
            };
            currentW = w; currentH = h;

            using (var sw = new StreamWriter(log, true)) {
                sw.WriteLine("  => Sunshine direct mode." +
                             " machine=" + machineName +
                             " user="    + userName + "@" + domainName +
                             " lcid="    + lcid +
                             " res="     + w + "x" + h +
                             " ["        + resSource + "]");
            }
        } else {
            // ── DuoManagerService mode ────────────────────────────────────────────────
            // Called by DuoManagerService with 7 connection args.
            // Override args[5] (width) and args[6] (height) with the correct resolution.
            newArgs = (string[])args.Clone();

            if (args.Length >= 7) {
                int origWidth  = 0;
                int origHeight = 0;
                int.TryParse(args[5], out origWidth);
                int.TryParse(args[6], out origHeight);

                int targetW      = origWidth;
                int targetH      = origHeight;
                string resSource = null;

                // Priority 1: GET /launch mode= from debug log (exact Moonlight-requested resolution)
                // Priority 2: SUNSHINE_CLIENT_WIDTH/HEIGHT env vars (present if Apollo is the direct caller)
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
                currentW = targetW;
                currentH = targetH;

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
        }

        string realExe = Path.Combine(duoDir, "DuoRdp_orig.exe");

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
        psi.UseShellExecute = false;

        // ── Main loop ─────────────────────────────────────────────────────────────────
        // Launch DuoRdp_orig.exe, then poll the log every 5 seconds while the child
        // is alive. If Moonlight reconnects with a different resolution (new "mode --"
        // entry within the last 60 s), kill the child and relaunch with the new args.
        // This gives real-time resolution updates without restarting DuoManagerService.
        while (true) {
            psi.Arguments = BuildQuotedArgs(newArgs);

            using (var sw = new StreamWriter(log, true))
                sw.WriteLine("  => Calling: " + realExe + " " + psi.Arguments);

            var proc = Process.Start(psi);

            if (hJob != IntPtr.Zero)
                AssignProcessToJobObject(hJob, proc.Handle);

            bool resolutionChanged = false;

            // WaitForExit(5000) returns true if process exited, false on timeout (still alive).
            while (!proc.WaitForExit(5000)) {
                int rW, rH;
                if (TryReadMoonlightLaunchResolution(duoDir, out rW, out rH) &&
                    (rW != currentW || rH != currentH)) {

                    using (var sw = new StreamWriter(log, true))
                        sw.WriteLine("=== Resolution change detected: " +
                                     currentW + "x" + currentH +
                                     " -> " + rW + "x" + rH +
                                     ". Restarting DuoRdp_orig.exe.");

                    try { proc.Kill(); } catch { }
                    proc.WaitForExit();

                    currentW = rW; currentH = rH;
                    newArgs[newArgs.Length - 2] = rW.ToString();
                    newArgs[newArgs.Length - 1] = rH.ToString();
                    resolutionChanged = true;
                    break;
                }
            }

            if (!resolutionChanged)
                return proc.ExitCode;
            // else loop back and restart with the new resolution
        }
    }
}
