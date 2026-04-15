using System;
using System.Runtime.InteropServices;
using System.Text;

class TestHidHideDouble {
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

    static void Main() {
        IntPtr h = CreateFileW(@"\\.\HidHide", 0x80000000 | 0x40000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h == new IntPtr(-1)) {
            h = CreateFileW(@"\\.\HidHide", 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        }
        if (h == new IntPtr(-1)) { Console.WriteLine("CreateFile falhou"); return; }

        byte[] activeOn = new byte[] { 1 };
        uint r;
        DeviceIoControl(h, IOCTL_SET_ACTIVE, activeOn, 1, null, 0, out r, IntPtr.Zero);

        string dev1 = @"HID\VID_054C&PID_05C4&REV_0100\2&130C1E12&0&0000";
        string dev2 = @"USB\VID_054C&PID_05C4&REV_0100\1&79f5d87&0&01";
        
        string payload = dev1 + "\0" + dev2 + "\0\0";
        byte[] data = Encoding.Unicode.GetBytes(payload);
        DeviceIoControl(h, IOCTL_SET_BLACKLIST, data, (uint)data.Length, null, 0, out r, IntPtr.Zero);
        
        Console.WriteLine("Blacklist setada com:");
        Console.WriteLine(dev1);
        Console.WriteLine(dev2);
        Console.WriteLine("Pressione ENTER para fechar o handle e sair...");
        Console.ReadLine();
        CloseHandle(h);
    }
}
