using System;
using System.Runtime.InteropServices;

class TestHidHide {
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        byte[] lpInBuffer, uint nInBufferSize,
        byte[] lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    const uint GENERIC_READ = 0x80000000;
    const uint FILE_SHARE_ALL = 7;
    const uint OPEN_EXISTING = 3;

    // HidHide IOCTLs
    const uint IOCTL_GET_BLACKLIST = 0x80016008;
    const uint IOCTL_SET_BLACKLIST = 0x8001600C;
    const uint IOCTL_GET_ACTIVE    = 0x80016010;
    const uint IOCTL_SET_ACTIVE    = 0x80016014;

    static void Main() {
        Console.WriteLine("Testando acesso ao device HidHide...");

        IntPtr h = CreateFileW(@"\\.\HidHide", GENERIC_READ, FILE_SHARE_ALL,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

        int err = Marshal.GetLastWin32Error();
        if (h == new IntPtr(-1)) {
            Console.WriteLine("CreateFile FALHOU. Win32 Error: " + err);
            if (err == 5)  Console.WriteLine("=> ACCESS_DENIED");
            if (err == 2)  Console.WriteLine("=> FILE_NOT_FOUND");
            if (err == 32) Console.WriteLine("=> SHARING_VIOLATION");
            return;
        }

        Console.WriteLine("CreateFile SUCESSO! Handle: " + h);

        // Ler estado do cloak (GET_ACTIVE)
        byte[] outBuf = new byte[1];
        uint returned;
        bool ok = DeviceIoControl(h, IOCTL_GET_ACTIVE, null, 0, outBuf, 1, out returned, IntPtr.Zero);
        if (ok) {
            Console.WriteLine("Cloak ativo: " + (outBuf[0] != 0 ? "SIM" : "NAO"));
        } else {
            Console.WriteLine("GET_ACTIVE falhou. Error: " + Marshal.GetLastWin32Error());
        }

        // Ativar o cloak
        byte[] inBuf = new byte[] { 1 }; // TRUE = ativar
        ok = DeviceIoControl(h, IOCTL_SET_ACTIVE, inBuf, 1, null, 0, out returned, IntPtr.Zero);
        Console.WriteLine("SET_ACTIVE (cloak on): " + (ok ? "OK" : "FALHOU err=" + Marshal.GetLastWin32Error()));

        // Ler blacklist atual
        byte[] bigBuf = new byte[4096];
        ok = DeviceIoControl(h, IOCTL_GET_BLACKLIST, null, 0, bigBuf, 4096, out returned, IntPtr.Zero);
        if (ok) {
            Console.WriteLine("Blacklist (" + returned + " bytes):");
            // Multi-string: null-terminated unicode strings, double-null at end
            string all = System.Text.Encoding.Unicode.GetString(bigBuf, 0, (int)returned);
            foreach (string s in all.Split('\0')) {
                if (!string.IsNullOrEmpty(s)) Console.WriteLine("  " + s);
            }
        } else {
            Console.WriteLine("GET_BLACKLIST falhou. Error: " + Marshal.GetLastWin32Error());
        }

        // Adicionar nosso device à blacklist
        string deviceId = @"HID\VID_054C&PID_05C4&REV_0100\2&130C1E12&0&0000";
        Console.WriteLine("\nAdicionando device ao blacklist: " + deviceId);

        // Construir multi-string: device + \0 + \0 (double null terminator)
        string multiStr = deviceId + "\0\0";
        byte[] setData = System.Text.Encoding.Unicode.GetBytes(multiStr);

        ok = DeviceIoControl(h, IOCTL_SET_BLACKLIST, setData, (uint)setData.Length, null, 0, out returned, IntPtr.Zero);
        Console.WriteLine("SET_BLACKLIST: " + (ok ? "OK" : "FALHOU err=" + Marshal.GetLastWin32Error()));

        // Verificar blacklist após
        ok = DeviceIoControl(h, IOCTL_GET_BLACKLIST, null, 0, bigBuf, 4096, out returned, IntPtr.Zero);
        if (ok) {
            Console.WriteLine("Blacklist apos update (" + returned + " bytes):");
            string all = System.Text.Encoding.Unicode.GetString(bigBuf, 0, (int)returned);
            foreach (string s in all.Split('\0')) {
                if (!string.IsNullOrEmpty(s)) Console.WriteLine("  " + s);
            }
        }

        CloseHandle(h);
        Console.WriteLine("\nHandle fechado. Teste concluido.");
    }
}
