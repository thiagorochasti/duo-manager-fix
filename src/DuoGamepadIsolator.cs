// DuoGamepadIsolator — Isola controles virtuais ViGEmBus por sessão de streaming
//
// Lógica: quando um device ViGEmBus é detectado, aplica dois bloqueios:
//   1. DACL (bloqueia acesso HID via file API):
//        DENY  [usuário da sessão console]   ← bloqueia o usuário principal
//        ALLOW EVERYONE                      ← permite Games, SYSTEM, etc.
//   2. HidHide (bloqueia acesso XInput/kernel — requer HidHide instalado):
//        Blacklist: adiciona o device ao HidHide
//        Whitelist dinâmica: processos da sessão RDP são adicionados automaticamente
//
// Bug A fix: _devInstProcessed é limpo quando a última interface do device é removida,
//            garantindo que o reciclo (CM_Disable/Enable) aconteça em cada reconexão.
//
// Cenários tratados:
//   - Serviço inicia com sessão já ativa (InitialScan)
//   - Callback de novo device (modo zero-CPU)
//   - Loop infinito de reciclo: _recycleUntil impede reprocessamento
//   - Múltiplas interfaces HID do mesmo device (Col01, Col02...) via _devInstProcessed
//   - Reconexão rápida do Moonlight (dentro da janela de reciclo)
//   - Console user muda: redetecta em nova conexão
//   - Polling fallback quando CM_Register_Notification falha
//   - ViGEmBus não encontrado: fallback por "IG_"/"VIGEM" no DeviceID
//   - HidHide ausente: funciona sem isolamento XInput (degrada graciosamente)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using Microsoft.Win32;

class DuoGamepadIsolator : ServiceBase {

    // =====================================================================
    // P/Invoke — CfgMgr32
    // =====================================================================

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Register_Notification(
        ref CM_NOTIFY_FILTER pFilter, IntPtr pContext,
        CM_NOTIFY_CALLBACK pCallback, out IntPtr pNotifyContext);

    [DllImport("CfgMgr32.dll")]
    static extern int CM_Unregister_Notification(IntPtr NotifyContext);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(
        out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Auto)]
    static extern int CM_Get_Device_ID(
        uint dnDevInst, StringBuilder Buffer, uint BufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Disable_DevNode(uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Enable_DevNode(uint dnDevInst, uint ulFlags);

    // =====================================================================
    // P/Invoke — SetupDi
    // =====================================================================

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(
        ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr DeviceInfoSet, IntPtr DeviceInfoData,
        ref Guid InterfaceClassGuid, uint MemberIndex,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize,
        out uint RequiredSize, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    // =====================================================================
    // P/Invoke — Kernel32 / Advapi32 / WtsApi32
    // =====================================================================

    [DllImport("kernel32.dll")]
    static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll")]
    static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool WTSQuerySessionInformation(
        IntPtr hServer, uint SessionId, uint WTSInfoClass,
        out IntPtr ppBuffer, out uint pBytesReturned);

    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool InitializeAcl(IntPtr pAcl, uint nAclLength, uint dwAclRevision);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AddAccessDeniedAce(
        IntPtr pAcl, uint dwAceRevision, uint AccessMask, IntPtr pSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AddAccessAllowedAce(
        IntPtr pAcl, uint dwAceRevision, uint AccessMask, IntPtr pSid);

    [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern uint SetNamedSecurityInfo(
        string pObjectName, uint ObjectType, uint SecurityInfo,
        IntPtr psidOwner, IntPtr psidGroup, IntPtr pDacl, IntPtr pSacl);

    // Converte SDDL em binary security descriptor (para escrita no registro)
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string StringSecurityDescriptor, uint Revision,
        out IntPtr pSecurityDescriptor, out uint SecurityDescriptorSize);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr hMem);

    // HidHide — acesso direto via IOCTL (não usa CLI para evitar deadlocks de handle)
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        byte[] lpInBuffer, uint nInBufferSize,
        byte[] lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    // Converte path de aplicação (ex: C:\foo.exe) para NT full image name (\Device\HarddiskVolume...)
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, uint ucchMax);

    // =====================================================================
    // Estruturas
    // =====================================================================

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate int CM_NOTIFY_CALLBACK(
        IntPtr hNotify, IntPtr Context, int Action,
        IntPtr EventData, int EventDataSize);

    [StructLayout(LayoutKind.Sequential)]
    struct CM_NOTIFY_FILTER {
        public uint cbSize;
        public uint Flags;
        public uint FilterType;
        public uint Reserved;
        public Guid ClassGuid;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 384)]
        public byte[] _padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    // =====================================================================
    // Constantes
    // =====================================================================

    const uint CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE = 0;
    const int  CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL = 0;
    const int  CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL = 1;
    const uint DIGCF_PRESENT         = 0x00000002;
    const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    const uint SE_FILE_OBJECT        = 1;
    const uint DACL_SECURITY_INFORMATION           = 0x00000004;
    const uint PROTECTED_DACL_SECURITY_INFORMATION = 0x80000000;
    const uint ACL_REVISION          = 2;
    const uint GENERIC_ALL           = 0x10000000;
    const uint WTS_USERNAME          = 5;

    const int RECYCLE_WINDOW_MS = 4000;

    static readonly Guid HID_GUID = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");

    const string LOG_PATH     = @"C:\Users\Public\duo_isolator.log";
    const string SERVICE_NAME = "DuoGamepadIsolator";
    const string HIDHIDE_CLI  = @"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe";
    const string SUNSHINE_EXE = @"C:\Program Files\Duo\sunshine.exe";

    // HidHide IOCTL codes (de FilterDriverProxy.cpp)
    // CTL_CODE(DeviceType=32769, Function, METHOD_BUFFERED=0, FILE_READ_DATA=1)
    const uint IOCTL_HH_GET_WHITELIST = 0x80016000;
    const uint IOCTL_HH_SET_WHITELIST = 0x80016004;
    const uint IOCTL_HH_GET_BLACKLIST = 0x80016008;
    const uint IOCTL_HH_SET_BLACKLIST = 0x8001600C;
    const uint IOCTL_HH_GET_ACTIVE    = 0x80016010;
    const uint IOCTL_HH_SET_ACTIVE    = 0x80016014;

    const uint GENERIC_READ_ACCESS   = 0x80000000;
    const uint FILE_SHARE_RWD        = 7; // READ | WRITE | DELETE
    const uint OPEN_EXISTING_DISP    = 3;

    // Dispositivos fisicos redirecionados via RDP que devem ser bloqueados no HOST.
    // O DS4 (DualShock 4) aparece como HID nativo quando redirecionado, e o Steam
    // do admin o detecta. Bloqueamos via HidHide para que apenas a sessao RDP o veja.
    static readonly string[] PHYSICAL_BLOCK_VIDPIDS = new string[] {
        // Sony PlayStation
        "VID_054C&PID_05C4",  // DualShock 4 (1st gen)
        "VID_054C&PID_09CC",  // DualShock 4 (2nd gen)
        "VID_054C&PID_0CE6",  // DualSense (PS5)
        "VID_054C&PID_0DF2",  // DualSense Edge (PS5)
        // Microsoft Xbox
        "VID_045E&PID_028E",  // Xbox 360 Controller
        "VID_045E&PID_02FF",  // Xbox One Controller (USB)
        "VID_045E&PID_02EA",  // Xbox One S Controller (USB)
        "VID_045E&PID_0B12",  // Xbox Series X|S Controller (USB)
        "VID_045E&PID_0B13",  // Xbox Series X|S Controller (Bluetooth)
    };

    // =====================================================================
    // Estado
    // =====================================================================

    IntPtr             _hNotify = IntPtr.Zero;
    CM_NOTIFY_CALLBACK _callbackDelegate;
    Thread             _pollingThread;
    Thread             _hidHideThread;
    volatile bool      _running;
    bool               _hidHideAvailable;
    // NOTA: NÃO manter handle persistente ao HidHide — driver só permite 1 conexão.
    // Usar OpenHidHideHandle() para abrir/fechar por operação.
    readonly object    _lock = new object();
    uint               _vigemBusInst = 0;

    readonly HashSet<string>             _done          = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> _recycleUntil = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<uint>               _devInstProcessed = new HashSet<uint>();

    // Bug A fix: rastreia symlink→parent e contagem de interfaces ativas por parent.
    // Quando a última interface de um device é removida, o parent é liberado de
    // _devInstProcessed, garantindo que a próxima reconexão dispare o reciclo.
    readonly Dictionary<string, uint> _symLinkToParent  = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<uint, int>    _parentIfaceCount = new Dictionary<uint, int>();

    // HidHide: devices ocultados (para restaurar no Stop)
    readonly HashSet<string> _hiddenDevices   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Devices em processo de reciclo (CM_Disable/Enable) — não remover da blacklist durante REMOVAL
    readonly HashSet<string> _recyclingDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Registry security: instanceIds onde escrevemos SD no registro (para restaurar ao parar)
    readonly HashSet<string> _deviceIdsWithRegistrySecurity = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Dispositivos fisicos bloqueados (DS4/DualSense redirecionados via RDP)
    readonly HashSet<string> _physicalBlockedDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Dispositivos fisicos "locais" — presentes ANTES de qualquer sessao RDP.
    // Estes pertencem ao admin do host e NAO devem ser bloqueados.
    readonly HashSet<string> _localPhysicalDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    volatile bool _baselineComplete = false;

    public DuoGamepadIsolator() { ServiceName = SERVICE_NAME; }

    // =====================================================================
    // Entry point
    // =====================================================================

    static void Main(string[] args) {
        if (args.Length > 0) {
            switch (args[0]) {
                case "--install":   Install();   return;
                case "--uninstall": Uninstall(); return;
                case "--run":
                    Console.WriteLine("DuoGamepadIsolator — modo console. Ctrl+C para parar.");
                    var svc = new DuoGamepadIsolator();
                    Console.CancelKeyPress += (s, e) => { svc._running = false; e.Cancel = true; };
                    svc.Start();
                    while (svc._running) Thread.Sleep(500);
                    svc.Stop2();
                    return;
            }
        }
        ServiceBase.Run(new DuoGamepadIsolator());
    }

    protected override void OnStart(string[] args) { Start(); }
    protected override void OnStop()              { Stop2(); }

    // =====================================================================
    // Inicialização
    // =====================================================================

    void FindViGEmBus() {
        try {
            using (var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\ROOT\SYSTEM")) {
                if (key == null) { Log("AVISO: ROOT\\SYSTEM nao encontrado."); return; }
                foreach (var sub in key.GetSubKeyNames()) {
                    using (var dev = key.OpenSubKey(sub)) {
                        if (dev == null) continue;
                        var svc = dev.GetValue("Service") as string;
                        if (svc == null) continue;
                        if (svc.IndexOf("vigem", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        string instanceId = "ROOT\\SYSTEM\\" + sub;
                        uint inst;
                        if (CM_Locate_DevNodeW(out inst, instanceId, 0) == 0) {
                            _vigemBusInst = inst;
                            Log("ViGEmBus: " + instanceId + " devInst=" + inst + " Service=" + svc);
                        }
                        return;
                    }
                }
            }
        } catch (Exception ex) { Log("ERRO FindViGEmBus: " + ex.Message); }
        Log("AVISO: ViGEmBus nao encontrado no registro. Usando fallback textual.");
    }

    void Start() {
        _running = true;
        TruncateLog();
        ClearState();
        FindViGEmBus();
        InitHidHide();

        _callbackDelegate = new CM_NOTIFY_CALLBACK(OnDeviceEvent);
        var filter = new CM_NOTIFY_FILTER {
            cbSize     = (uint)Marshal.SizeOf(typeof(CM_NOTIFY_FILTER)),
            FilterType = CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE,
            Flags      = 0, Reserved = 0, ClassGuid = HID_GUID,
            _padding   = new byte[384]
        };

        int rc = CM_Register_Notification(ref filter, IntPtr.Zero, _callbackDelegate, out _hNotify);
        if (rc == 0) {
            Log("Modo: kernel callback. CPU = 0% em idle.");
            new Thread(InitialScan) { IsBackground = true, Name = "InitialScan" }.Start();
        } else {
            _hNotify = IntPtr.Zero;
            Log("CM_Register_Notification rc=" + rc + ". Usando smart polling.");
            _pollingThread = new Thread(SmartPollingLoop) { IsBackground = true, Name = "Polling" };
            _pollingThread.Start();
        }
    }

    void ClearState() {
        string[] toRestore;
        lock (_lock) {
            toRestore = new string[_deviceIdsWithRegistrySecurity.Count];
            _deviceIdsWithRegistrySecurity.CopyTo(toRestore);
            _deviceIdsWithRegistrySecurity.Clear();
            _done.Clear();
            _recycleUntil.Clear();
            _devInstProcessed.Clear();
            _symLinkToParent.Clear();
            _parentIfaceCount.Clear();
            _physicalBlockedDevices.Clear();
            _localPhysicalDevices.Clear();
            _baselineComplete = false;
        }
        foreach (var id in toRestore) RestoreDeviceSecurityInRegistry(id);
    }

    // Registra todos os controles fisicos alvo ja presentes no sistema.
    // Estes sao considerados "locais" (do admin) e nao serao bloqueados.
    void BaselinePhysicalDevices() {
        var guid = HID_GUID;
        IntPtr devs = SetupDiGetClassDevs(
            ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devs == new IntPtr(-1)) { _baselineComplete = true; return; }
        int count = 0;
        try {
            uint idx = 0;
            while (true) {
                var iface = new SP_DEVICE_INTERFACE_DATA {
                    cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA))
                };
                if (!SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref guid, idx, ref iface)) break;
                idx++;
                var devInfo = new SP_DEVINFO_DATA {
                    cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA))
                };
                string path = GetDevicePath(devs, ref iface, ref devInfo);
                if (path == null) continue;
                if (IsPhysicalBlockTarget(path)) {
                    string instanceId = SymLinkToInstanceId(path);
                    lock (_lock) { _localPhysicalDevices.Add(instanceId); }
                    count++;
                    Log("Baseline: controle local registrado (" + instanceId + ")");
                }
            }
        } finally { SetupDiDestroyDeviceInfoList(devs); }
        _baselineComplete = true;
        Log("Baseline: " + count + " controle(s) fisico(s) local(is) registrado(s).");
    }

    void InitialScan() {
        Thread.Sleep(1000);
        Log("Baseline de dispositivos fisicos locais...");
        BaselinePhysicalDevices();
        Log("Scan inicial...");
        ScanAllHidDevices();
        Log("Scan inicial concluido.");
    }

    void Stop2() {
        _running = false;
        if (_hNotify != IntPtr.Zero) {
            CM_Unregister_Notification(_hNotify);
            _hNotify = IntPtr.Zero;
        }
        if (_pollingThread != null) _pollingThread.Join(5000);
        if (_hidHideThread  != null) _hidHideThread.Join(3000);
        ShutdownHidHide();
        Log("Parado.");
    }

    // =====================================================================
    // HidHide — Isolamento XInput via IOCTL direto
    // =====================================================================
    // Motivo: o HidHideCLI abre handle exclusivo ao device \\.\ HidHide.
    // Se outro processo (HidHideWatchdog, Duo Manager, ou uma instância
    // anterior do CLI) já tiver o handle, o CLI trava indefinidamente.
    // Usando DeviceIoControl direto com FILE_SHARE flags, evitamos deadlocks.

    // Abre handle transiente ao HidHide — DEVE ser fechado pelo chamador.
    // Retorna IntPtr.Zero se falhar.
    IntPtr OpenHidHideHandle() {
        IntPtr h = CreateFileW(@"\\.\HidHide", GENERIC_READ_ACCESS, FILE_SHARE_RWD,
            IntPtr.Zero, OPEN_EXISTING_DISP, 0, IntPtr.Zero);
        if (h == new IntPtr(-1)) return IntPtr.Zero;
        return h;
    }

    void InitHidHide() {
        // Testa se o driver está acessível (abre e fecha imediatamente)
        IntPtr test = OpenHidHideHandle();
        if (test == IntPtr.Zero) {
            int err = Marshal.GetLastWin32Error();
            Log("HidHide: driver nao acessivel (err=" + err + ") — isolamento XInput desabilitado.");
            _hidHideAvailable = false;
            return;
        }
        CloseHandle(test);
        _hidHideAvailable = true;
        Log("HidHide: driver acessivel (handle transiente — sem lock persistente).");

        // Limpa blacklist de execucoes anteriores (crash, servico morto no meio, etc.)
        try {
            var stale = HidHideGetMultiString(IOCTL_HH_GET_BLACKLIST);
            if (stale.Count > 0) {
                HidHideSetMultiString(IOCTL_HH_SET_BLACKLIST, new List<string>());
                Log("HidHide: " + stale.Count + " entrada(s) obsoleta(s) removida(s) da blacklist.");
            }
        } catch (Exception ex) { Log("HidHide init limpeza erro: " + ex.Message); }

        // Ativar cloak
        HidHideSetActive(true);
        Log("HidHide: cloak ativado (XInput bloqueado para processos nao-whitelistados).");

        // Whitelist do Apollo/Sunshine — precisa sempre ver o device
        string sun1 = @"C:\Program Files\Duo\sunshine.exe";
        string sun2 = @"C:\Program Files\Apollo\sunshine.exe";
        if (File.Exists(sun1)) HidHideAddToWhitelist(sun1);
        else if (File.Exists(sun2)) HidHideAddToWhitelist(sun2);

        // Thread que monitora processos das sessoes RDP e atualiza a whitelist
        _hidHideThread = new Thread(HidHideWhitelistLoop) {
            IsBackground = true, Name = "HidHideWhitelist"
        };
        _hidHideThread.Start();
    }

    void ShutdownHidHide() {
        // Restaura DACL padrao no registro
        string[] toRestore;
        lock (_lock) {
            toRestore = new string[_deviceIdsWithRegistrySecurity.Count];
            _deviceIdsWithRegistrySecurity.CopyTo(toRestore);
            _deviceIdsWithRegistrySecurity.Clear();
        }
        foreach (var id in toRestore) RestoreDeviceSecurityInRegistry(id);

        if (!_hidHideAvailable) return;

        // Limpa blacklist completa — não usa _hiddenDevices porque callbacks tardios
        // podem tê-lo esvaziado antes do shutdown. Zerando tudo garantimos estado limpo.
        try {
            var antes = HidHideGetMultiString(IOCTL_HH_GET_BLACKLIST);
            HidHideSetMultiString(IOCTL_HH_SET_BLACKLIST, new List<string>());
            Log("HidHide: blacklist limpa (" + antes.Count + " entradas removidas).");
        } catch (Exception ex) { Log("HidHide shutdown blacklist erro: " + ex.Message); }

        lock (_lock) { _hiddenDevices.Clear(); }

        // Desativa cloak
        HidHideSetActive(false);
        Log("HidHide: cloak desativado.");
    }

    // ---- IOCTL helpers ----

    void HidHideSetActive(bool active) {
        if (!_hidHideAvailable) return;
        IntPtr h = OpenHidHideHandle();
        if (h == IntPtr.Zero) { Log("HidHide SET_ACTIVE: handle falhou"); return; }
        try {
            byte[] buf = new byte[] { (byte)(active ? 1 : 0) };
            uint ret;
            if (!DeviceIoControl(h, IOCTL_HH_SET_ACTIVE, buf, 1, null, 0, out ret, IntPtr.Zero))
                Log("HidHide SET_ACTIVE erro=" + Marshal.GetLastWin32Error());
        } finally { CloseHandle(h); }
    }

    List<string> HidHideGetMultiString(uint ioctl) {
        var result = new List<string>();
        if (!_hidHideAvailable) return result;
        IntPtr h = OpenHidHideHandle();
        if (h == IntPtr.Zero) return result;
        try {
            byte[] buf = new byte[65536];
            uint ret;
            if (!DeviceIoControl(h, ioctl, null, 0, buf, (uint)buf.Length, out ret, IntPtr.Zero))
                return result;
            string all = Encoding.Unicode.GetString(buf, 0, (int)ret);
            foreach (string s in all.Split('\0'))
                if (!string.IsNullOrEmpty(s)) result.Add(s);
        } finally { CloseHandle(h); }
        return result;
    }

    void HidHideSetMultiString(uint ioctl, List<string> items) {
        if (!_hidHideAvailable) return;
        IntPtr h = OpenHidHideHandle();
        if (h == IntPtr.Zero) { Log("HidHide SET ioctl=0x" + ioctl.ToString("X") + ": handle falhou"); return; }
        try {
            var sb = new StringBuilder();
            foreach (var s in items) { sb.Append(s); sb.Append('\0'); }
            sb.Append('\0'); // double-null terminator
            byte[] data = Encoding.Unicode.GetBytes(sb.ToString());
            uint ret;
            if (!DeviceIoControl(h, ioctl, data, (uint)data.Length, null, 0, out ret, IntPtr.Zero))
                Log("HidHide SET ioctl=0x" + ioctl.ToString("X") + " erro=" + Marshal.GetLastWin32Error());
        } finally { CloseHandle(h); }
    }

    void HidHideAddToBlacklist(string instanceId) {
        if (!_hidHideAvailable) return;
        var current = HidHideGetMultiString(IOCTL_HH_GET_BLACKLIST);
        // Não duplicar
        foreach (var s in current)
            if (s.Equals(instanceId, StringComparison.OrdinalIgnoreCase)) return;
        current.Add(instanceId);
        HidHideSetMultiString(IOCTL_HH_SET_BLACKLIST, current);
    }

    void HidHideRemoveFromBlacklist(string instanceId) {
        if (!_hidHideAvailable) return;
        var current = HidHideGetMultiString(IOCTL_HH_GET_BLACKLIST);
        var keep = new List<string>();
        foreach (var s in current)
            if (!s.Equals(instanceId, StringComparison.OrdinalIgnoreCase)) keep.Add(s);
        HidHideSetMultiString(IOCTL_HH_SET_BLACKLIST, keep);
    }

    // Converte path Win32 (C:\foo.exe) para NT full image name (\Device\HarddiskVolumeN\foo.exe)
    // necessário para a whitelist do HidHide que opera com NT paths.
    static string ToNtImagePath(string win32Path) {
        try {
            string drive = System.IO.Path.GetPathRoot(win32Path);
            if (string.IsNullOrEmpty(drive)) return win32Path;
            string letter = drive.TrimEnd('\\');
            var sb = new StringBuilder(260);
            uint len = QueryDosDevice(letter, sb, 260);
            if (len == 0) return win32Path;
            string ntDrive = sb.ToString(); // ex: \Device\HarddiskVolume3
            return ntDrive + win32Path.Substring(letter.Length);
        } catch { return win32Path; }
    }

    void HidHideAddToWhitelist(string exePath) {
        if (!_hidHideAvailable || !File.Exists(exePath)) return;
        string ntPath = ToNtImagePath(exePath);
        var current = HidHideGetMultiString(IOCTL_HH_GET_WHITELIST);
        foreach (var s in current)
            if (s.Equals(ntPath, StringComparison.OrdinalIgnoreCase)) return;
        current.Add(ntPath);
        HidHideSetMultiString(IOCTL_HH_SET_WHITELIST, current);
        Log("HidHide whitelist +: " + exePath + " (" + ntPath + ")");
    }

    void HidHideRemoveFromWhitelist(string exePath) {
        if (!_hidHideAvailable) return;
        string ntPath = ToNtImagePath(exePath);
        var current = HidHideGetMultiString(IOCTL_HH_GET_WHITELIST);
        var keep = new List<string>();
        bool found = false;
        foreach (var s in current) {
            if (s.Equals(ntPath, StringComparison.OrdinalIgnoreCase)) { found = true; continue; }
            keep.Add(s);
        }
        if (found) {
            HidHideSetMultiString(IOCTL_HH_SET_WHITELIST, keep);
            Log("HidHide whitelist -: " + exePath);
        }
    }

    // Monitora processos nas sessoes RDP (nao-console) e mantem a whitelist sincronizada.
    // IMPORTANTE: paths que tambem rodam na sessao console (ex: steam.exe do admin) sao
    // excluidos da whitelist para evitar que o host veja o controle virtual pelo mesmo exe.
    void HidHideWhitelistLoop() {
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (_running) {
            for (int i = 0; i < 20 && _running; i++) Thread.Sleep(100);
            try {
                uint consoleSession = WTSGetActiveConsoleSessionId();
                var rdpPaths     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var consolePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var proc in Process.GetProcesses()) {
                    try {
                        uint sessionId;
                        if (!ProcessIdToSessionId((uint)proc.Id, out sessionId)) continue;
                        if (sessionId == 0) continue;
                        string path = null;
                        try { path = proc.MainModule != null ? proc.MainModule.FileName : null; }
                        catch { continue; }
                        if (string.IsNullOrEmpty(path)) continue;
                        if (sessionId == consoleSession) consolePaths.Add(path);
                        else rdpPaths.Add(path);
                    } catch { }
                    finally { try { proc.Dispose(); } catch { } }
                }

                foreach (var path in rdpPaths) {
                    if (consolePaths.Contains(path)) continue;
                    if (!tracked.Contains(path)) {
                        HidHideAddToWhitelist(path);
                        tracked.Add(path);
                    }
                }

                var toRemove = new List<string>();
                foreach (var path in tracked)
                    if (!rdpPaths.Contains(path) || consolePaths.Contains(path)) toRemove.Add(path);
                foreach (var path in toRemove) {
                    HidHideRemoveFromWhitelist(path);
                    tracked.Remove(path);
                }
            } catch (Exception ex) { Log("HidHide whitelist loop erro: " + ex.Message); }
        }
    }

    // Converte \\?\HID#VID_045E&PID_028E&IG_01#3&966d8b0&0&0000#{guid}
    //     para HID\VID_045E&PID_028E&IG_01\3&966D8B0&0&0000
    static string SymLinkToInstanceId(string symLink) {
        string s = symLink;
        if (s.StartsWith(@"\\?\")) s = s.Substring(4);
        int last = s.LastIndexOf('#');
        if (last > 0) s = s.Substring(0, last);
        return s.Replace('#', '\\').ToUpperInvariant();
    }

    // =====================================================================
    // Modo A: Kernel callback (CM_Register_Notification)
    // =====================================================================

    int OnDeviceEvent(IntPtr hNotify, IntPtr Context, int Action, IntPtr EventData, int EventDataSize) {
        try {
            if (EventData == IntPtr.Zero) return 0;
            string symLink = Marshal.PtrToStringUni(new IntPtr(EventData.ToInt64() + 24));
            if (string.IsNullOrEmpty(symLink)) return 0;

            if (Action == CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL) {
                string instanceId = SymLinkToInstanceId(symLink);
                bool unhide = false;

                lock (_lock) {
                    _done.Remove(symLink);

                    // Bug A fix: decrementa contagem de interfaces do parent.
                    uint parent;
                    if (_symLinkToParent.TryGetValue(symLink, out parent)) {
                        _symLinkToParent.Remove(symLink);
                        int cnt;
                        _parentIfaceCount.TryGetValue(parent, out cnt);
                        cnt = cnt > 1 ? cnt - 1 : 0;
                        if (cnt == 0) {
                            _parentIfaceCount.Remove(parent);
                            _devInstProcessed.Remove(parent);
                            Log("Device removido — parent " + parent + " liberado (proxima chegada fara reciclo).");
                        } else {
                            _parentIfaceCount[parent] = cnt;
                        }
                    }

                    // NÃO remover da blacklist se:
                    //   - o device está em reciclo (CM_Disable/Enable)
                    //   - o device é um controle fisico bloqueado (DS4/DualSense/Xbox via RDP)
                    if (_hidHideAvailable && !_recyclingDevices.Contains(instanceId)
                        && !_physicalBlockedDevices.Contains(instanceId)) {
                        if (_hiddenDevices.Remove(instanceId)) unhide = true;

                        // Remover tambem o pai USB (se registrado) ao remover o HID
                        string usbId = GetDeviceId(parent);
                        if (!string.IsNullOrEmpty(usbId) && !_recyclingDevices.Contains(usbId) && _hiddenDevices.Remove(usbId)) {
                            HidHideRemoveFromBlacklist(usbId);
                            Log("  HidHide: USB pai restaurado (" + usbId + ")");
                        }
                    }
                    if (_physicalBlockedDevices.Contains(instanceId)) {
                        Log("FISICO: REMOVAL ignorado — mantendo bloqueio (" + instanceId + ")");
                    }
                }

                if (unhide) {
                    HidHideRemoveFromBlacklist(instanceId);
                    Log("  HidHide: HID restaurado (" + instanceId + ")");
                }
                return 0;
            }

            if (Action != CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL) return 0;

            // Dispositivo fisico alvo (DS4/DualSense via RDP)? Bloqueia direto, sem passar pelo fluxo ViGEm.
            if (IsPhysicalBlockTarget(symLink)) {
                ProcessPhysicalBlockDevice(symLink);
                lock (_lock) { _done.Add(symLink); }
                return 0;
            }

            lock (_lock) {
                if (_done.Contains(symLink)) return 0;
                DateTime until;
                if (_recycleUntil.TryGetValue(symLink, out until) && DateTime.Now < until) {
                    _done.Add(symLink);
                    return 0;
                }
            }

            string instancePath = SymLinkToInstancePath(symLink);
            uint devInst;
            if (CM_Locate_DevNodeW(out devInst, instancePath, 0) != 0) return 0;
            if (!IsViGEmDevice(devInst)) return 0;

            uint busDevInst = GetViGEmBusChild(devInst);
            ProcessDevice(symLink, devInst, busDevInst);
        } catch (Exception ex) { Log("ERRO callback: " + ex.Message); }
        return 0;
    }

    // =====================================================================
    // Modo B: Smart polling (fallback quando CM_Register_Notification falha)
    // =====================================================================

    void SmartPollingLoop() {
        Log("Smart polling: 5s idle / 200ms com streaming.");
        while (_running) {
            bool streaming = Process.GetProcessesByName("sunshine").Length > 0;
            if (streaming) {
                ScanAllHidDevices();
                SleepInterruptible(200);
            } else {
                // Sem sessão ativa: restaura devices ocultos e limpa estado
                if (_hidHideAvailable) {
                    string[] toUnhide;
                    lock (_lock) {
                        toUnhide = new string[_hiddenDevices.Count];
                        _hiddenDevices.CopyTo(toUnhide);
                        _hiddenDevices.Clear();
                    }
                    foreach (var id in toUnhide)
                        HidHideRemoveFromBlacklist(id);
                }
                ClearState();
                SleepInterruptible(5000);
            }
        }
    }

    void SleepInterruptible(int ms) {
        for (int i = 0; i < ms / 100 && _running; i++) Thread.Sleep(100);
    }

    void ScanAllHidDevices() {
        var guid = HID_GUID;
        IntPtr devs = SetupDiGetClassDevs(
            ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devs == new IntPtr(-1)) return;
        int countAll = 0, countVigem = 0, countProcessed = 0;
        try {
            uint idx = 0;
            while (true) {
                var iface = new SP_DEVICE_INTERFACE_DATA {
                    cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA))
                };
                if (!SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref guid, idx, ref iface)) break;
                idx++;
                var devInfo = new SP_DEVINFO_DATA {
                    cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA))
                };
                string path = GetDevicePath(devs, ref iface, ref devInfo);
                if (path == null) continue;
                countAll++;

                lock (_lock) { if (_done.Contains(path)) continue; }

                // Dispositivo fisico alvo (DS4/DualSense/Xbox via RDP)? Bloqueia direto.
                if (IsPhysicalBlockTarget(path)) {
                    ProcessPhysicalBlockDevice(path);
                    lock (_lock) { _done.Add(path); }
                    countProcessed++;
                    continue;
                }

                if (!IsViGEmDevice(devInfo.DevInst)) continue;
                countVigem++;

                uint busDevInst = GetViGEmBusChild(devInfo.DevInst);
                Log("  Scan encontrou device ativo: " + path);
                ProcessDevice(path, devInfo.DevInst, busDevInst);
                countProcessed++;
            }
            Log("  Scan HID: " + countAll + " presentes, " + countVigem + " ViGEm, " + countProcessed + " processados novos.");
        } finally { SetupDiDestroyDeviceInfoList(devs); }
    }

    string GetDevicePath(IntPtr devs, ref SP_DEVICE_INTERFACE_DATA iface, ref SP_DEVINFO_DATA devInfo) {
        uint needed = 0;
        var tmp = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
        SetupDiGetDeviceInterfaceDetail(devs, ref iface, IntPtr.Zero, 0, out needed, ref tmp);
        if (needed < 5) return null;

        uint bufSize = needed + 32;
        IntPtr buf = Marshal.AllocHGlobal((int)bufSize);
        try {
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(devs, ref iface, buf, bufSize, out needed, ref devInfo))
                return null;
            string path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
            if (path == null || !path.StartsWith(@"\\?\")) return null;
            return path;
        } finally { Marshal.FreeHGlobal(buf); }
    }

    // =====================================================================
    // Lógica principal: DACL + HidHide + reciclo
    // =====================================================================

    void ProcessDevice(string devicePath, uint devInst, uint busDevInst) {
        SecurityIdentifier consoleSid = GetConsoleUserSid();

        uint directParent = devInst;
        CM_Get_Parent(out directParent, devInst, 0);

        bool isFirstInterface;
        lock (_lock) {
            // Bug A fix: registra mapeamento symlink→parent e contagem de interfaces.
            if (!_symLinkToParent.ContainsKey(devicePath)) {
                _symLinkToParent[devicePath] = directParent;
                int cnt;
                _parentIfaceCount.TryGetValue(directParent, out cnt);
                _parentIfaceCount[directParent] = cnt + 1;
            }
            isFirstInterface = !_devInstProcessed.Contains(directParent);
            if (isFirstInterface) _devInstProcessed.Add(directParent);
        }

        Log("ViGEmBus device: " + devicePath +
            (isFirstInterface ? " [PRIMEIRO — vai reciclar]" : " [interface adicional, sem reciclo]"));

        if (consoleSid != null)
            Log("  DENY console SID: " + consoleSid.Value);
        else
            Log("  Sem usuario console — apenas ALLOW EVERYONE.");

        // Bloqueio 1: DACL — bloqueia acesso HID via file API
        bool ok = ApplyDacl(devicePath, consoleSid);
        if (!ok) Log("  DACL falhou (erro=5 esperado em reciclo) — HidHide ira cobrir.");
        else     Log("  DACL OK (DENY console + ALLOW EVERYONE).");

        // Bloqueio 2: HidHide — bloqueia acesso XInput e qualquer path de kernel
        // Steam Input acessa a interface USB bruta para controles Sony DS4, contornando a blacklist HID.
        // Ocultamos tambem o "busDevInst", que e o root USB gerado pelo ViGEmBus.
        string instanceId = SymLinkToInstanceId(devicePath);
        string usbParentId = GetDeviceId(busDevInst);

        if (_hidHideAvailable) {
            lock (_lock) {
                if (_hiddenDevices.Add(instanceId)) {
                    HidHideAddToBlacklist(instanceId);
                    Log("  HidHide: HID ocultado (" + instanceId + ")");
                }
                if (!string.IsNullOrEmpty(usbParentId) && _hiddenDevices.Add(usbParentId)) {
                    HidHideAddToBlacklist(usbParentId);
                    Log("  HidHide: USB pai ocultado (" + usbParentId + ")");
                }
            }
        }

        lock (_lock) {
            _done.Add(devicePath);
            _recycleUntil[devicePath] = DateTime.Now.AddMilliseconds(RECYCLE_WINDOW_MS);
        }

        if (isFirstInterface) {
            // Escreve DACL no registro ANTES do reciclo. Quando CM_Enable re-enumera o device,
            // o driver HID lê o SD do registro → nossa DACL restritiva persiste após o Enable.
            WriteDeviceSecurityToRegistry(instanceId, consoleSid);

            // Marca devices como "em reciclo" para que a REMOVAL transiente
            // não os remova da blacklist do HidHide
            lock (_lock) { 
                _recyclingDevices.Add(instanceId); 
                if (!string.IsNullOrEmpty(usbParentId)) _recyclingDevices.Add(usbParentId);
            }

            Log("  Reciclando device (disable+enable) para fechar handles existentes...");
            int rcDis = CM_Disable_DevNode(busDevInst, 0);
            Thread.Sleep(400);
            int rcEna = CM_Enable_DevNode(busDevInst, 0);
            Log("  Reciclo: Disable=" + rcDis + " Enable=" + rcEna +
                (rcDis == 0 && rcEna == 0 ? " OK" : " AVISO — verifique privilegios"));

                // Re-aplica HidHide blacklist ANTES de limpar _recyclingDevices.
            // Isso protege contra callbacks REMOVAL tardios do CM_Disable que chegam
            // depois do CM_Enable — eles veriam _recyclingDevices vazio e removeriam
            // da blacklist prematuramente.
            if (_hidHideAvailable) {
                HidHideAddToBlacklist(instanceId);
                lock (_lock) { _hiddenDevices.Add(instanceId); }
                Log("  HidHide: blacklist HID reconfirmada pos-reciclo.");

                if (!string.IsNullOrEmpty(usbParentId)) {
                    HidHideAddToBlacklist(usbParentId);
                    lock (_lock) { _hiddenDevices.Add(usbParentId); }
                    Log("  HidHide: blacklist USB reconfirmada pos-reciclo.");
                }
            }

            // Mantém a flag de reciclo por mais 2s após o Enable para absorver
            // callbacks REMOVAL tardios que o CM_Disable/Enable dispara.
            // Durante esse período, qualquer REMOVAL desse device é ignorado.
            string _instanceId  = instanceId;
            string _usbParentId = usbParentId;
            new Thread(() => {
                Thread.Sleep(2000);
                lock (_lock) {
                    _recyclingDevices.Remove(_instanceId);
                    if (!string.IsNullOrEmpty(_usbParentId)) _recyclingDevices.Remove(_usbParentId);
                }
            }) { IsBackground = true, Name = "RecycleCleanup" }.Start();
        }
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    uint GetViGEmBusChild(uint devInst) {
        if (_vigemBusInst == 0) return devInst;
        uint cur = devInst;
        uint prev = devInst;
        for (int i = 0; i < 5; i++) {
            uint parent;
            if (CM_Get_Parent(out parent, cur, 0) != 0) break;
            if (parent == _vigemBusInst) return cur;
            prev = cur;
            cur  = parent;
        }
        return devInst;
    }

    SecurityIdentifier GetConsoleUserSid() {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) return null;
        IntPtr pBuf = IntPtr.Zero;
        uint len = 0;
        try {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, WTS_USERNAME, out pBuf, out len))
                return null;
            string username = Marshal.PtrToStringUni(pBuf);
            if (string.IsNullOrEmpty(username)) return null;
            Log("  Console sessionId=" + sessionId + " user=" + username);
            return (SecurityIdentifier)new NTAccount(username).Translate(typeof(SecurityIdentifier));
        } catch (Exception ex) {
            Log("  GetConsoleUserSid erro: " + ex.Message);
            return null;
        } finally {
            if (pBuf != IntPtr.Zero) WTSFreeMemory(pBuf);
        }
    }

    static string SymLinkToInstancePath(string symLink) {
        string s = symLink;
        if (s.StartsWith(@"\\?\")) s = s.Substring(4);
        int last = s.LastIndexOf('#');
        if (last > 0) s = s.Substring(0, last);
        return s.Replace('#', '\\');
    }

    // Verifica se o symLink ou instanceId contem um VID/PID de dispositivo fisico
    // que deve ser bloqueado no host (DS4, DualSense redirecionados via RDP).
    static bool IsPhysicalBlockTarget(string path) {
        if (string.IsNullOrEmpty(path)) return false;
        for (int i = 0; i < PHYSICAL_BLOCK_VIDPIDS.Length; i++)
            if (path.IndexOf(PHYSICAL_BLOCK_VIDPIDS[i], StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        return false;
    }

    // Processa um dispositivo fisico alvo: aplica HidHide blacklist sem reciclo.
    // Diferente dos virtuais ViGEm, dispositivos fisicos redirecionados via RDP
    // nao precisam de DACL nem reciclo — apenas o bloqueio HidHide e suficiente
    // para impedir que o Steam do host os detecte.
    void ProcessPhysicalBlockDevice(string devicePath) {
        string instanceId = SymLinkToInstanceId(devicePath);
        if (!_hidHideAvailable) {
            Log("FISICO: " + instanceId + " detectado mas HidHide indisponivel.");
            return;
        }

        // Se o baseline ja foi feito e este dispositivo estava presente antes de
        // qualquer sessao RDP, ele pertence ao admin do host — nao bloquear.
        lock (_lock) {
            if (_baselineComplete && _localPhysicalDevices.Contains(instanceId)) {
                Log("FISICO: " + instanceId + " e local (admin) — nao bloqueado.");
                return;
            }
            if (_physicalBlockedDevices.Contains(instanceId)) return;
            _physicalBlockedDevices.Add(instanceId);
            _hiddenDevices.Add(instanceId);
        }

        HidHideAddToBlacklist(instanceId);
        Log("FISICO: bloqueado no host via HidHide (" + instanceId + ")");

        // Tambem bloqueia o parent USB se existir
        uint devInst;
        if (CM_Locate_DevNodeW(out devInst, SymLinkToInstancePath(devicePath), 0) == 0) {
            uint parent;
            if (CM_Get_Parent(out parent, devInst, 0) == 0) {
                string parentId = GetDeviceId(parent);
                if (!string.IsNullOrEmpty(parentId)) {
                    lock (_lock) { _hiddenDevices.Add(parentId); }
                    HidHideAddToBlacklist(parentId);
                    Log("FISICO: USB pai bloqueado (" + parentId + ")");
                }
            }
        }
    }

    bool IsViGEmDevice(uint devInst) {
        uint cur = devInst;
        for (int level = 0; level < 4; level++) {
            if (_vigemBusInst != 0 && cur == _vigemBusInst) return true;
            var sb = new StringBuilder(512);
            if (CM_Get_Device_ID(cur, sb, 512, 0) != 0) return false;
            string id = sb.ToString();
            if (id.IndexOf("IG_",   StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (id.IndexOf("VIGEM", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            uint parent;
            if (CM_Get_Parent(out parent, cur, 0) != 0) return false;
            cur = parent;
        }
        return false;
    }

    string GetDeviceId(uint devInst) {
        var sb = new StringBuilder(512);
        if (CM_Get_Device_ID(devInst, sb, 512, 0) == 0) return sb.ToString();
        return null;
    }

    bool ApplyDacl(string devicePath, SecurityIdentifier consoleSid) {
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        int aclSize  = 8;
        if (consoleSid != null) aclSize += 4 + 4 + consoleSid.BinaryLength;
        aclSize += 4 + 4 + everyone.BinaryLength;

        IntPtr pAcl   = Marshal.AllocHGlobal(aclSize);
        var    pinned = new List<GCHandle>();
        try {
            if (!InitializeAcl(pAcl, (uint)aclSize, ACL_REVISION)) return false;

            if (consoleSid != null) {
                byte[] b = new byte[consoleSid.BinaryLength];
                consoleSid.GetBinaryForm(b, 0);
                GCHandle h = GCHandle.Alloc(b, GCHandleType.Pinned);
                pinned.Add(h);
                AddAccessDeniedAce(pAcl, ACL_REVISION, GENERIC_ALL, h.AddrOfPinnedObject());
            }

            byte[] eb = new byte[everyone.BinaryLength];
            everyone.GetBinaryForm(eb, 0);
            GCHandle eh = GCHandle.Alloc(eb, GCHandleType.Pinned);
            pinned.Add(eh);
            AddAccessAllowedAce(pAcl, ACL_REVISION, GENERIC_ALL, eh.AddrOfPinnedObject());

            uint err = SetNamedSecurityInfo(
                devicePath, SE_FILE_OBJECT,
                DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
                IntPtr.Zero, IntPtr.Zero, pAcl, IntPtr.Zero);
            if (err != 0) Log("  SetNamedSecurityInfo erro=" + err);
            return err == 0;
        } finally {
            foreach (var h in pinned) h.Free();
            Marshal.FreeHGlobal(pAcl);
        }
    }

    // Escreve security descriptor no registro ANTES do CM_Enable.
    // Quando CM_Enable causa re-enumeração, o driver HID carrega o SD do registro
    // em vez do padrão permissivo do Windows — nossa DACL restritiva persiste.
    void WriteDeviceSecurityToRegistry(string instanceId, SecurityIdentifier consoleSid) {
        try {
            // SDDL: P = DACL protegida, DENY console user GENERIC_ALL, ALLOW Everyone GENERIC_ALL
            string sddl = consoleSid != null
                ? "D:P(D;;GA;;;" + consoleSid.Value + ")(A;;GA;;;WD)"
                : "D:P(A;;GA;;;WD)";

            IntPtr pSd;
            uint sdSize;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out pSd, out sdSize)) {
                Log("  WriteRegSec: ConvertSddl falhou, err=" + Marshal.GetLastWin32Error());
                return;
            }
            try {
                byte[] sdBytes = new byte[sdSize];
                Marshal.Copy(pSd, sdBytes, 0, (int)sdSize);

                // Caminho: HKLM\SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters
                // instanceId formato: HID\VID_045E&PID_028E&IG_01\3&966D8B0&0&0000
                string regPath = @"SYSTEM\CurrentControlSet\Enum\" + instanceId + @"\Device Parameters";
                using (var key = Registry.LocalMachine.OpenSubKey(regPath, true)) {
                    if (key == null) {
                        // Tenta criar a subchave Device Parameters se não existir
                        string parent = @"SYSTEM\CurrentControlSet\Enum\" + instanceId;
                        using (var pk = Registry.LocalMachine.OpenSubKey(parent, true)) {
                            if (pk == null) { Log("  WriteRegSec: instância não encontrada: " + instanceId); return; }
                            using (var created = pk.CreateSubKey("Device Parameters"))
                                created.SetValue("Security", sdBytes, RegistryValueKind.Binary);
                        }
                    } else {
                        key.SetValue("Security", sdBytes, RegistryValueKind.Binary);
                    }
                    Log("  WriteRegSec: DACL persistida no registro (" + instanceId + ")");
                }
            } finally {
                LocalFree(pSd);
            }

            lock (_lock) { _deviceIdsWithRegistrySecurity.Add(instanceId); }
        } catch (Exception ex) { Log("  WriteRegSec erro: " + ex.Message); }
    }

    // Remove a entry Security do registro para restaurar DACL padrão após sessão encerrar.
    void RestoreDeviceSecurityInRegistry(string instanceId) {
        try {
            string regPath = @"SYSTEM\CurrentControlSet\Enum\" + instanceId + @"\Device Parameters";
            using (var key = Registry.LocalMachine.OpenSubKey(regPath, true)) {
                if (key == null) return;
                key.DeleteValue("Security", false);
                Log("  RestoreRegSec: Security removida do registro (" + instanceId + ")");
            }
        } catch (Exception ex) { Log("  RestoreRegSec erro: " + ex.Message); }
    }

    // =====================================================================
    // Log
    // =====================================================================

    static void Log(string msg) {
        string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + msg;
        Console.WriteLine(line);
        try { File.AppendAllText(LOG_PATH, line + Environment.NewLine); } catch { }
    }

    static void TruncateLog(int maxLines = 300) {
        try {
            if (!File.Exists(LOG_PATH)) return;
            string[] lines = File.ReadAllLines(LOG_PATH);
            if (lines.Length <= maxLines) return;
            string[] keep = new string[maxLines];
            Array.Copy(lines, lines.Length - maxLines, keep, 0, maxLines);
            File.WriteAllLines(LOG_PATH, keep);
        } catch { }
    }

    // =====================================================================
    // Instalação / remoção
    // =====================================================================

    static void Install() {
        string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
        Exec("sc.exe", "create " + SERVICE_NAME +
            " binPath= \"" + exe + "\" start= auto DisplayName= \"Duo Gamepad Isolator\"");
        Exec("sc.exe", "description " + SERVICE_NAME +
            " \"Isola controles virtuais ViGEmBus para a sessao de streaming ativa\"");
        Exec("sc.exe", "failure " + SERVICE_NAME + " reset= 86400 actions= restart/5000/restart/10000/restart/30000");
        Exec("sc.exe", "start " + SERVICE_NAME);
        Console.WriteLine("Servico instalado com restart automatico. Log: " + LOG_PATH);
    }

    static void Uninstall() {
        Exec("sc.exe", "stop "   + SERVICE_NAME);
        Exec("sc.exe", "delete " + SERVICE_NAME);
        Console.WriteLine("Servico removido.");
    }

    static void Exec(string exe, string args) {
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false }).WaitForExit();
    }
}
