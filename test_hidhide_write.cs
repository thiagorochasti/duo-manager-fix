using System;
using System.Runtime.InteropServices;
using System.Text;

class TestHidHideWrite {
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    static extern IntPtr CreateFileW(string f, uint a, uint s, IntPtr p, uint c, uint fl, IntPtr t);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inB, uint inS, byte[] outB, uint outS, out uint ret, IntPtr ov);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    const uint IOCTL_GET_BLACKLIST = 0x80016008;
    const uint IOCTL_SET_BLACKLIST = 0x8001600C;
    const uint IOCTL_GET_ACTIVE    = 0x80016010;
    const uint IOCTL_SET_ACTIVE    = 0x80016014;

    static void Main(string[] args) {
        // Teste 1: GENERIC_READ | GENERIC_WRITE
        uint GENERIC_RW = 0x80000000 | 0x40000000;
        Console.WriteLine("Teste com GENERIC_READ|WRITE...");
        IntPtr h = CreateFileW(@"\\.\HidHide", GENERIC_RW, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        int err1 = Marshal.GetLastWin32Error();
        if (h != new IntPtr(-1)) {
            Console.WriteLine("  SUCESSO com RW! Handle: " + h);
            
            // Setar blacklist e cloak
            byte[] activeOn = new byte[] { 1 };
            uint r;
            DeviceIoControl(h, IOCTL_SET_ACTIVE, activeOn, 1, null, 0, out r, IntPtr.Zero);
            Console.WriteLine("  SET_ACTIVE on: OK");
            
            string devId = @"HID\VID_054C&PID_05C4&REV_0100\2&130C1E12&0&0000";
            string multi = devId + "\0\0";
            byte[] data = Encoding.Unicode.GetBytes(multi);
            DeviceIoControl(h, IOCTL_SET_BLACKLIST, data, (uint)data.Length, null, 0, out r, IntPtr.Zero);
            Console.WriteLine("  SET_BLACKLIST: OK");
            
            // FECHAR handle
            CloseHandle(h);
            Console.WriteLine("  Handle FECHADO.");
            
            // Reabrir e verificar se persistiu
            Console.WriteLine("\n  Reabrindo para verificar persistencia...");
            IntPtr h2 = CreateFileW(@"\\.\HidHide", 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h2 != new IntPtr(-1)) {
                byte[] ab = new byte[1];
                DeviceIoControl(h2, IOCTL_GET_ACTIVE, null, 0, ab, 1, out r, IntPtr.Zero);
                Console.WriteLine("  Cloak apos reopen: " + (ab[0] != 0 ? "SIM" : "NAO"));
                
                byte[] buf = new byte[65536];
                DeviceIoControl(h2, IOCTL_GET_BLACKLIST, null, 0, buf, 65536, out r, IntPtr.Zero);
                Console.WriteLine("  Blacklist apos reopen (" + r + " bytes):");
                string all = Encoding.Unicode.GetString(buf, 0, (int)r);
                foreach (string s in all.Split('\0'))
                    if (!string.IsNullOrEmpty(s)) Console.WriteLine("    " + s);
                if (r <= 2) Console.WriteLine("    (vazia)");
                
                CloseHandle(h2);
            } else {
                Console.WriteLine("  Reopen falhou: " + Marshal.GetLastWin32Error());
            }
        } else {
            Console.WriteLine("  FALHOU com RW. Err: " + err1);
        }
    }
}
