// HidHideNative — Acesso direto ao driver HidHide via IOCTL (zero latencia)
//
// Abre um handle persistente para \\.\HidHide no startup do servico.
// Todos os comandos sao enviados via DeviceIoControl em ~0.1-0.5ms,
// eliminando a race condition contra a Steam (que captura em 2-5ms).
//
// IOCTLs oficiais do HidHide v1.5.230:
//   0x80016000 — GET_DEVICE_LIST
//   0x80016004 — SET_DEVICE_LIST
//   0x80016008 — GET_APPLICATION_LIST
//   0x8001600C — SET_APPLICATION_LIST
//   0x80016010 — GET_ACTIVE (cloak)
//   0x80016014 — SET_ACTIVE (cloak)

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class HidHideNative {

    static readonly string DEVICE_PATH = @"\\.\HidHide";
    static readonly object _lock = new object();

    static IntPtr _hDevice = IntPtr.Zero;
    static bool _available;
    static bool _checked;

    // IOCTLs do HidHide driver
    const uint IOCTL_GET_DEVICE_LIST      = 0x80016000;
    const uint IOCTL_SET_DEVICE_LIST      = 0x80016004;
    const uint IOCTL_GET_APPLICATION_LIST = 0x80016008;
    const uint IOCTL_SET_APPLICATION_LIST = 0x8001600C;
    const uint IOCTL_GET_ACTIVE           = 0x80016010;
    const uint IOCTL_SET_ACTIVE           = 0x80016014;

    const uint GENERIC_READ  = 0x80000000;
    const uint GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ  = 0x00000001;
    const uint FILE_SHARE_WRITE = 0x00000002;
    const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern IntPtr CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll")]
    static extern uint GetLastError();

    // =====================================================================
    // Lifecycle
    // =====================================================================

    public static bool IsAvailable {
        get {
            if (!_checked) {
                _available = File.Exists(
                    @"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe");
                _checked = true;
            }
            return _available;
        }
    }

    public static bool Open() {
        if (_hDevice != IntPtr.Zero && _hDevice != new IntPtr(-1)) return true;
        lock (_lock) {
            _hDevice = CreateFile(DEVICE_PATH,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (_hDevice == new IntPtr(-1)) {
                _hDevice = IntPtr.Zero;
                return false;
            }
            return true;
        }
    }

    public static void Close() {
        lock (_lock) {
            if (_hDevice != IntPtr.Zero && _hDevice != new IntPtr(-1)) {
                CloseHandle(_hDevice);
                _hDevice = IntPtr.Zero;
            }
        }
    }

    // =====================================================================
    // Device List (blacklist)
    // =====================================================================

    public static List<string> GetHiddenDevices() {
        return GetList(IOCTL_GET_DEVICE_LIST);
    }

    public static void HideDevice(string instanceId) {
        if (string.IsNullOrEmpty(instanceId)) return;
        var list = GetHiddenDevices();
        string norm = NormalizeId(instanceId);
        if (ContainsId(list, norm)) return;
        list.Add(norm);
        SetList(IOCTL_SET_DEVICE_LIST, list);
    }

    public static void UnhideDevice(string instanceId) {
        if (string.IsNullOrEmpty(instanceId)) return;
        var list = GetHiddenDevices();
        string norm = NormalizeId(instanceId);
        RemoveId(list, norm);
        SetList(IOCTL_SET_DEVICE_LIST, list);
    }

    public static void HideDevices(IEnumerable<string> instanceIds) {
        var list = GetHiddenDevices();
        bool changed = false;
        foreach (var id in instanceIds) {
            if (string.IsNullOrEmpty(id)) continue;
            string norm = NormalizeId(id);
            if (!ContainsId(list, norm)) {
                list.Add(norm);
                changed = true;
            }
        }
        if (changed) SetList(IOCTL_SET_DEVICE_LIST, list);
    }

    public static void UnhideDevices(IEnumerable<string> instanceIds) {
        var list = GetHiddenDevices();
        bool changed = false;
        foreach (var id in instanceIds) {
            if (string.IsNullOrEmpty(id)) continue;
            string norm = NormalizeId(id);
            if (RemoveId(list, norm)) changed = true;
        }
        if (changed) SetList(IOCTL_SET_DEVICE_LIST, list);
    }

    public static void ClearHiddenDevices() {
        SetList(IOCTL_SET_DEVICE_LIST, new List<string>());
    }

    // =====================================================================
    // Application List (whitelist)
    // =====================================================================

    public static List<string> GetWhitelistedApps() {
        return GetList(IOCTL_GET_APPLICATION_LIST);
    }

    public static void AddToWhitelist(string exePath) {
        if (string.IsNullOrEmpty(exePath)) return;
        var list = GetWhitelistedApps();
        string norm = NormalizePath(exePath);
        if (ContainsPath(list, norm)) return;
        list.Add(norm);
        SetList(IOCTL_SET_APPLICATION_LIST, list);
    }

    public static void RemoveFromWhitelist(string exePath) {
        if (string.IsNullOrEmpty(exePath)) return;
        var list = GetWhitelistedApps();
        string norm = NormalizePath(exePath);
        if (RemovePath(list, norm))
            SetList(IOCTL_SET_APPLICATION_LIST, list);
    }

    // =====================================================================
    // Cloak state
    // =====================================================================

    public static bool GetCloakState() {
        lock (_lock) {
            if (!EnsureOpen()) return true;
            uint returned;
            // Driver pode retornar BOOLEAN(1) ou BOOL/DWORD(4); usamos 4 para seguranca
            IntPtr buf = Marshal.AllocHGlobal(4);
            try {
                bool ok = DeviceIoControl(_hDevice, IOCTL_GET_ACTIVE,
                    IntPtr.Zero, 0, buf, 4, out returned, IntPtr.Zero);
                if (ok && returned >= 1)
                    return Marshal.ReadByte(buf) != 0;
                return true; // assume ativado se nao conseguir ler
            } finally { Marshal.FreeHGlobal(buf); }
        }
    }

    public static void SetCloak(bool active) {
        lock (_lock) {
            if (!EnsureOpen()) return;
            uint returned;
            // Driver pode esperar BOOLEAN(1) ou BOOL/DWORD(4); usamos 4 para seguranca
            IntPtr buf = Marshal.AllocHGlobal(4);
            try {
                Marshal.WriteByte(buf, active ? (byte)1 : (byte)0);
                DeviceIoControl(_hDevice, IOCTL_SET_ACTIVE,
                    buf, 4, IntPtr.Zero, 0, out returned, IntPtr.Zero);
            } finally { Marshal.FreeHGlobal(buf); }
        }
    }

    // =====================================================================
    // Low-level helpers
    // =====================================================================

    static bool EnsureOpen() {
        if (_hDevice != IntPtr.Zero && _hDevice != new IntPtr(-1)) return true;
        return Open();
    }

    static List<string> GetList(uint ioctl) {
        lock (_lock) {
            if (!EnsureOpen()) return new List<string>();
            // Tenta com buffer crescente (8KB -> 64KB) se a lista for grande
            int[] sizes = { 8192, 65536 };
            for (int attempt = 0; attempt < sizes.Length; attempt++) {
                IntPtr buf = Marshal.AllocHGlobal(sizes[attempt]);
                try {
                    uint returned;
                    bool ok = DeviceIoControl(_hDevice, ioctl,
                        IntPtr.Zero, 0, buf, (uint)sizes[attempt], out returned, IntPtr.Zero);
                    if (!ok) {
                        uint err = GetLastError();
                        if (err == 122 && attempt < sizes.Length - 1) continue; // ERROR_INSUFFICIENT_BUFFER
                        return new List<string>();
                    }
                    if (returned < 2) return new List<string>();

                    // O driver pode retornar: [Header 8 bytes][Multi-sz] ou apenas [Multi-sz]
                    // Header: uint TotalListLengthInBytes + uint ListCount
                    int offset = 0;
                    if (returned >= 8) {
                        uint totalLen = (uint)Marshal.ReadInt32(buf);
                        uint listCount = (uint)Marshal.ReadInt32(new IntPtr(buf.ToInt64() + 4));
                        // Heuristica: se os 8 primeiros bytes parecem um header valido, pula-os
                        if (totalLen > 0 && totalLen <= returned && listCount <= 10000) {
                            offset = 8;
                        }
                    }
                    return MultiSzToList(new IntPtr(buf.ToInt64() + offset), (int)returned - offset);
                } finally { Marshal.FreeHGlobal(buf); }
            }
            return new List<string>();
        }
    }

    static void SetList(uint ioctl, List<string> items) {
        lock (_lock) {
            if (!EnsureOpen()) return;
            int size;
            IntPtr buf = ListToMultiSz(items, out size);
            try {
                uint returned;
                DeviceIoControl(_hDevice, ioctl,
                    buf, (uint)size, IntPtr.Zero, 0, out returned, IntPtr.Zero);
            } finally { Marshal.FreeHGlobal(buf); }
        }
    }

    static List<string> MultiSzToList(IntPtr ptr, int maxBytes) {
        var result = new List<string>();
        int offset = 0;
        while (offset < maxBytes - 1) {
            string s = Marshal.PtrToStringUni(new IntPtr(ptr.ToInt64() + offset));
            if (string.IsNullOrEmpty(s)) break;
            result.Add(s);
            offset += (s.Length + 1) * 2; // +1 para o null terminator
        }
        return result;
    }

    static IntPtr ListToMultiSz(List<string> items, out int byteSize) {
        // Calcula tamanho total em wchar_t
        int totalChars = 0;
        foreach (var s in items) totalChars += s.Length + 1; // +1 para null de cada string
        totalChars += 1; // null final duplo

        byteSize = totalChars * 2; // UTF-16 = 2 bytes por char
        IntPtr ptr = Marshal.AllocHGlobal(byteSize);
        int offset = 0;
        foreach (var s in items) {
            byte[] bytes = Encoding.Unicode.GetBytes(s + "\0");
            Marshal.Copy(bytes, 0, new IntPtr(ptr.ToInt64() + offset), bytes.Length);
            offset += bytes.Length;
        }
        // Null final
        Marshal.WriteInt16(new IntPtr(ptr.ToInt64() + offset), 0);
        return ptr;
    }

    // =====================================================================
    // String helpers
    // =====================================================================

    static string NormalizeId(string s) {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Replace("\\\\", "\\");
    }

    static string NormalizePath(string s) {
        if (string.IsNullOrEmpty(s)) return s;
        return Path.GetFullPath(s).ToUpperInvariant();
    }

    static bool ContainsId(List<string> list, string id) {
        foreach (var s in list)
            if (string.Equals(s, id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static bool RemoveId(List<string> list, string id) {
        for (int i = 0; i < list.Count; i++) {
            if (string.Equals(list[i], id, StringComparison.OrdinalIgnoreCase)) {
                list.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    static bool ContainsPath(List<string> list, string path) {
        foreach (var s in list)
            if (string.Equals(s, path, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static bool RemovePath(List<string> list, string path) {
        for (int i = 0; i < list.Count; i++) {
            if (string.Equals(list[i], path, StringComparison.OrdinalIgnoreCase)) {
                list.RemoveAt(i);
                return true;
            }
        }
        return false;
    }
}
