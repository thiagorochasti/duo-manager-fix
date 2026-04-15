using System;
using System.Runtime.InteropServices;
using System.Text;

class TestHidHideStatus {
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    static extern IntPtr CreateFileW(string f, uint a, uint s, IntPtr p, uint c, uint fl, IntPtr t);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inB, uint inS, byte[] outB, uint outS, out uint ret, IntPtr ov);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    const uint IOCTL_GET_WHITELIST = 0x80016000;
    const uint IOCTL_GET_BLACKLIST = 0x80016008;
    const uint IOCTL_GET_ACTIVE    = 0x80016010;

    static void Main() {
        IntPtr h = CreateFileW(@"\\.\HidHide", 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h == new IntPtr(-1)) { Console.WriteLine("FALHOU err=" + Marshal.GetLastWin32Error()); return; }

        // Cloak
        byte[] ab = new byte[1]; uint r;
        DeviceIoControl(h, IOCTL_GET_ACTIVE, null, 0, ab, 1, out r, IntPtr.Zero);
        Console.WriteLine("=== CLOAK ATIVO: " + (ab[0] != 0 ? "SIM" : "NAO") + " ===\n");

        // Blacklist
        byte[] buf = new byte[65536];
        if (DeviceIoControl(h, IOCTL_GET_BLACKLIST, null, 0, buf, 65536, out r, IntPtr.Zero)) {
            Console.WriteLine("=== BLACKLIST (" + r + " bytes) ===");
            string all = Encoding.Unicode.GetString(buf, 0, (int)r);
            int i = 0;
            foreach (string s in all.Split('\0'))
                if (!string.IsNullOrEmpty(s)) Console.WriteLine("  [" + (i++) + "] " + s);
            if (i == 0) Console.WriteLine("  (vazia)");
        }
        Console.WriteLine();

        // Whitelist
        if (DeviceIoControl(h, IOCTL_GET_WHITELIST, null, 0, buf, 65536, out r, IntPtr.Zero)) {
            Console.WriteLine("=== WHITELIST (" + r + " bytes) ===");
            string all = Encoding.Unicode.GetString(buf, 0, (int)r);
            int i = 0;
            foreach (string s in all.Split('\0'))
                if (!string.IsNullOrEmpty(s)) Console.WriteLine("  [" + (i++) + "] " + s);
            if (i == 0) Console.WriteLine("  (vazia)");
        }

        CloseHandle(h);
    }
}
