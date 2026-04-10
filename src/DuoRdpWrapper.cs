using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

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

    static int Main(string[] args) {
        string log = @"C:\Users\Public\duordp_args.txt";
        using (var sw = new StreamWriter(log, true)) {
            sw.WriteLine("=== DuoRdpWrapper invocado: " + DateTime.Now);
            sw.WriteLine("Args count: " + args.Length);
            for (int i = 0; i < args.Length; i++) {
                sw.WriteLine("  [" + i + "] = " + args[i]);
            }
        }

        string[] newArgs = (string[])args.Clone();

        if (args.Length >= 7) {
            int origWidth  = 0;
            int origHeight = 0;
            int.TryParse(args[5], out origWidth);
            int.TryParse(args[6], out origHeight);

            if (origWidth < 3840 || origHeight < 2160) {
                newArgs[5] = "3840";
                newArgs[6] = "2160";
                using (var sw = new StreamWriter(log, true)) {
                    sw.WriteLine("  => Substituindo " + origWidth + "x" + origHeight + " por 2560x1440");
                }
            }
        }

        string realExe = @"C:\Program Files\Duo\DuoRdp_orig.exe";
        string quotedArgs = string.Join(" ", Array.ConvertAll(newArgs, delegate(string a) { return "\"" + a + "\""; }));

        using (var sw = new StreamWriter(log, true)) {
            sw.WriteLine("  => Chamando: " + realExe + " " + quotedArgs);
        }

        // Criar Job Object para garantir que o filho morra junto com o wrapper
        IntPtr hJob = CreateJobObject(IntPtr.Zero, null);
        if (hJob != IntPtr.Zero) {
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr pInfo = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(info, pInfo, false);
            SetInformationJobObject(hJob, JobObjectExtendedLimitInformation, pInfo, (uint)size);
            Marshal.FreeHGlobal(pInfo);
        }

        var psi = new ProcessStartInfo();
        psi.FileName = realExe;
        psi.Arguments = quotedArgs;
        psi.UseShellExecute = false;
        var proc = Process.Start(psi);

        if (hJob != IntPtr.Zero) {
            AssignProcessToJobObject(hJob, proc.Handle);
        }

        proc.WaitForExit();
        return proc.ExitCode;
    }
}
