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

    // Lê target_resolution de C:\Program Files\Duo\config\duo_wrapper.conf
    // Formato: target_resolution=1920x1080
    // Permite o usuário forçar uma resolução específica independente do monitor físico.
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

    // Lê SUNSHINE_CLIENT_WIDTH / SUNSHINE_CLIENT_HEIGHT injetadas pelo Sunshine em todos os processos filhos.
    // Esta é a fonte mais confiável: é a resolução exata que o Moonlight negociou.
    static bool TryReadSunshineEnvResolution(out int width, out int height) {
        width  = 0;
        height = 0;
        string w = Environment.GetEnvironmentVariable("SUNSHINE_CLIENT_WIDTH");
        string h = Environment.GetEnvironmentVariable("SUNSHINE_CLIENT_HEIGHT");
        if (string.IsNullOrEmpty(w) || string.IsNullOrEmpty(h)) return false;
        return int.TryParse(w, out width) && int.TryParse(h, out height) && width > 0 && height > 0;
    }

    // Lê a resolução exata pedida pelo Moonlight do request HTTP GET /launch?mode=WxHxFPS
    // gravado pelo Sunshine com min_log_level=debug. Lê só os últimos 512KB para não
    // bloquear em logs grandes. Busca do fim para o início (sessão mais recente).
    static bool TryReadMoonlightLaunchResolution(string duoDir, out int width, out int height) {
        width  = 0;
        height = 0;
        string logPath = Path.Combine(duoDir, "config", "Games.log");
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
            // Padrão real do debug log do Sunshine: "Debug: mode -- 2560x1600x60"
            // O parâmetro mode é logado individualmente após "DESTINATION :: /launch"
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

    // Le a resolucao de streaming do Games.log (Sunshine/Apollo com min_log_level=info).
    // O Sunshine registra "Desktop resolution [WxH]" antes de invocar DuoRdp.exe.
    // Busca do fim para o inicio para pegar a sessao mais recente.
    static bool TryReadMoonlightResolution(string duoDir, out int width, out int height) {
        width = 0;
        height = 0;
        string logPath = Path.Combine(duoDir, "config", "Games.log");
        if (!File.Exists(logPath)) return false;
        try {
            string[] lines = File.ReadAllLines(logPath);
            // Padrao principal: "Desktop resolution [1920x1080]"
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

    // Lê dd_manual_resolution do sunshine.conf do Apollo (fallback secundário).
    // Formato da linha: "dd_manual_resolution = 1920x1080" (ou sem espaços).
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
        string duoDir = @"C:\Program Files\Duo";

        using (var sw = new StreamWriter(log, true)) {
            sw.WriteLine("=== DuoRdpWrapper invocado: " + DateTime.Now);
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

            int targetW   = origWidth;
            int targetH   = origHeight;
            string resSource = null;

            // Prioridade 1: GET /launch?mode= do debug log (resolução EXATA do Moonlight)
            // Prioridade 2: env vars SUNSHINE_CLIENT_WIDTH/HEIGHT (reservado para futuro)
            // Prioridade 3: duo_wrapper.conf (override manual — fallback se log indisponível)
            // Prioridade 4: Desktop resolution do Games.log (resolução do RDP virtual display)
            // Prioridade 5: dd_manual_resolution do sunshine.conf (Apollo config estático)
            // Fallback: usa o que o Duo enviou
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
                    sw.WriteLine("  => Duo enviou " + origWidth + "x" + origHeight +
                                 ". Substituindo por " + targetW + "x" + targetH +
                                 " [" + resSource + "]");
                } else if (resSource != null) {
                    sw.WriteLine("  => Resolucao confirmada: " + targetW + "x" + targetH +
                                 " [" + resSource + "] — coincide com o que Duo enviou.");
                } else {
                    sw.WriteLine("  => Games.log nao encontrado. Usando resolucao do Duo: " +
                                 origWidth + "x" + origHeight);
                }
            }
        }

        string realExe    = Path.Combine(duoDir, "DuoRdp_orig.exe");
        string quotedArgs = string.Join(" ", Array.ConvertAll(newArgs,
            delegate(string a) { return "\"" + a + "\""; }));

        using (var sw = new StreamWriter(log, true)) {
            sw.WriteLine("  => Chamando: " + realExe + " " + quotedArgs);
        }

        // Job Object garante que o processo filho morra junto com o wrapper.
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
