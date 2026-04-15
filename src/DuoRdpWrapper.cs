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

    // Lê a resolução solicitada pelo Moonlight diretamente do log do Apollo.
    // O Apollo registra "clientViewportWd" e "clientViewportHt" no SDP durante o
    // handshake RTSP — ANTES de invocar DuoRdp.exe. Isso garante que a resolução
    // correta seja lida já na primeira conexão.
    // Busca do fim para o início para pegar a sessão mais recente.
    static bool TryReadMoonlightResolution(string duoDir, out int width, out int height) {
        width = 0;
        height = 0;
        string logPath = Path.Combine(duoDir, "config", "Games.log");
        if (!File.Exists(logPath)) return false;
        try {
            string[] lines = File.ReadAllLines(logPath);
            int foundW = 0, foundH = 0;
            for (int i = lines.Length - 1; i >= 0; i--) {
                if (foundW == 0) {
                    Match mw = Regex.Match(lines[i], @"clientViewportWd\s*:\s*(\d+)", RegexOptions.IgnoreCase);
                    if (mw.Success) foundW = int.Parse(mw.Groups[1].Value);
                }
                if (foundH == 0) {
                    Match mh = Regex.Match(lines[i], @"clientViewportHt\s*:\s*(\d+)", RegexOptions.IgnoreCase);
                    if (mh.Success) foundH = int.Parse(mh.Groups[1].Value);
                }
                if (foundW > 0 && foundH > 0) {
                    width  = foundW;
                    height = foundH;
                    return true;
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

            // Sempre tenta ler a resolução real negociada pelo Moonlight.
            // Prioridade 1: clientViewportWd/Ht do Games.log (gravado pelo Apollo antes de invocar DuoRdp.exe)
            // Prioridade 2: dd_manual_resolution do sunshine.conf
            // Fallback: usa o que o Duo enviou (qualquer valor, incluindo 640x480 ou resolução do monitor físico)
            int rW, rH;
            if (TryReadMoonlightResolution(duoDir, out rW, out rH)) {
                targetW   = rW;
                targetH   = rH;
                resSource = "Moonlight (Games.log)";
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
