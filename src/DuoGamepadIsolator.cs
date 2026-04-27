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

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Reenumerate_DevNode(uint dnDevInst, uint ulFlags);

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

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
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

    [DllImport("user32.dll", SetLastError = true)]
    static extern int BroadcastSystemMessage(uint flags, ref uint lpInfo, uint Msg, IntPtr wParam, IntPtr lParam);

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

    const int WM_DEVICECHANGE        = 0x219;
    const int DBT_DEVNODES_CHANGED   = 0x0007;
    const uint BSF_POSTMESSAGE       = 0x00000010;
    const uint BSM_APPLICATIONS      = 0x00000008;
    const uint CM_REENUMERATE_NORMAL = 0x00000000;

    const int RECYCLE_WINDOW_MS = 1000;
    const int PENDING_UNHIDE_SECONDS = 30;

    static readonly Guid HID_GUID  = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");
    static readonly Guid XUSB_GUID = new Guid("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");

    const string LOG_PATH     = @"C:\Users\Public\duo_isolator.log";
    const string SERVICE_NAME = "DuoGamepadIsolator";
    const string HIDHIDE_CLI  = @"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe";
    const string SUNSHINE_EXE = @"C:\Program Files\Duo\sunshine.exe";
    const string XUSB_PERSIST_DIR  = @"C:\ProgramData\DuoFix";
    const string XUSB_PERSIST_PATH = @"C:\ProgramData\DuoFix\xusb_blacklist.dat";

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
    IntPtr             _hNotifyXusb = IntPtr.Zero;
    CM_NOTIFY_CALLBACK _callbackDelegate;
    CM_NOTIFY_CALLBACK _callbackDelegateXusb;
    Thread             _pollingThread;
    Thread             _hidHideThread;
    Thread             _watchdogThread;   // HidHide watchdog anti-vazamento (Home button)
    volatile bool      _running;
    bool               _hidHideAvailable;
    // NOTA: Todas as operacoes HidHide usam HidHideCLI.exe (sem IOCTL direto).
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

    // Delayed unhide: devices removidos que ainda devem ficar na blacklist por N segundos.
    // Isso elimina a janela de visibilidade entre REMOVAL e ARRIVAL em reconexões rápidas (Moonlight).
    readonly Dictionary<string, DateTime> _pendingUnhide = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

    // Protecao contra loop infinito: registra quando um device foi processado pela ultima vez.
    // Se o mesmo symLink chegar em menos de 10s, ignora (evita reciclo infinito quando
    // CM_Disable falha com veto e o device e recriado pelo callback REMOVAL).
    readonly Dictionary<string, DateTime> _recentlyProcessed = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

    // Registry security: instanceIds onde escrevemos SD no registro (para restaurar ao parar)
    readonly HashSet<string> _deviceIdsWithRegistrySecurity = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Dispositivos fisicos bloqueados (DS4/DualSense redirecionados via RDP)
    readonly HashSet<string> _physicalBlockedDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Dispositivos fisicos "locais" — presentes ANTES de qualquer sessao RDP.
    // Estes pertencem ao admin do host e NAO devem ser bloqueados.
    readonly HashSet<string> _localPhysicalDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    volatile bool _baselineComplete = false;

    // XUSB blacklist PERSISTENTE em disco — sobrevive a reinicios do servico.
    // O ViGEmBus reutiliza InstanceId fixo (ex: USB\VID_045E&PID_028E\01).
    // Mantendo persistido, o device ja esta bloqueado antes de ser criado.
    readonly HashSet<string> _persistentXusbBlacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // XUSB devices desabilitados via CM_Disable_DevNode — reabilitados no shutdown.
    // Isso impede que a Steam detecte o controle no gap de criacao, e eh totalmente
    // reversivel: ao parar o servico, todos sao reabilitados.
    readonly HashSet<uint> _disabledXusbDevices = new HashSet<uint>();

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
        LoadPersistentXusbBlacklist();
        PreemptiveViGEmBlacklist();

        // Eleva prioridade para vencer a race condition contra a Steam (2-5ms)
        try {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            Log("Prioridade elevada: processo=High, thread=Highest.");
        } catch (Exception ex) {
            Log("AVISO: nao foi possivel elevar prioridade: " + ex.Message);
        }

        // Inicia watchdog HidHide que detecta e corrige vazamentos em ~200ms
        if (HidHideNative.IsAvailable) {
            _watchdogThread = new Thread(HidHideWatchdogLoop) {
                IsBackground = true, Name = "HidHideWatchdog"
            };
            _watchdogThread.Start();
            Log("HidHide: watchdog nativo ativado (50ms polling anti-Home via SetupDi).");
        }

        // Inicia rotator de log (limpa a cada 1h, mantem 2 dias)
        new Thread(LogRotatorLoop) { IsBackground = true, Name = "LogRotator" }.Start();
        Log("LogRotator: thread iniciada (rotacao a cada 1h, mantem 2 dias).");

        _callbackDelegate = new CM_NOTIFY_CALLBACK(OnDeviceEvent);
        var filter = new CM_NOTIFY_FILTER {
            cbSize     = (uint)Marshal.SizeOf(typeof(CM_NOTIFY_FILTER)),
            FilterType = CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE,
            Flags      = 0, Reserved = 0, ClassGuid = HID_GUID,
            _padding   = new byte[384]
        };

        int rc = CM_Register_Notification(ref filter, IntPtr.Zero, _callbackDelegate, out _hNotify);
        if (rc == 0) {
            Log("Modo: kernel callback HID. CPU = 0% em idle.");
            new Thread(InitialScan) { IsBackground = true, Name = "InitialScan" }.Start();
        } else {
            _hNotify = IntPtr.Zero;
            Log("CM_Register_Notification HID rc=" + rc + ". Usando smart polling.");
            _pollingThread = new Thread(SmartPollingLoop) { IsBackground = true, Name = "Polling" };
            _pollingThread.Start();
            return;
        }

        // Callback separado para XUSB/XInput — reage instantaneamente a criacao do device XInput
        _callbackDelegateXusb = new CM_NOTIFY_CALLBACK(OnXusbEvent);
        var filterXusb = new CM_NOTIFY_FILTER {
            cbSize     = (uint)Marshal.SizeOf(typeof(CM_NOTIFY_FILTER)),
            FilterType = CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE,
            Flags      = 0, Reserved = 0, ClassGuid = XUSB_GUID,
            _padding   = new byte[384]
        };
        int rcXusb = CM_Register_Notification(ref filterXusb, IntPtr.Zero, _callbackDelegateXusb, out _hNotifyXusb);
        if (rcXusb == 0) {
            Log("Modo: kernel callback XUSB ativado.");
        } else {
            _hNotifyXusb = IntPtr.Zero;
            Log("CM_Register_Notification XUSB rc=" + rcXusb + " (XInput nao sera bloqueado preemptivamente).");
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
            _pendingUnhide.Clear();
            _recentlyProcessed.Clear();
            // NOTA: _persistentXusbBlacklist e _disabledXusbDevices NAO sao limpos —
            // sobrevivem a reinicios do servico e sao restaurados no shutdown.
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

    // =====================================================================
    // Persistencia XUSB — sobrevive a reinicios do servico
    // =====================================================================

    // =====================================================================
    // Pre-Creation Blacklist — bloqueia IDs ViGEm ANTES de qualquer device ser criado
    // =====================================================================
    // O ViGEmBus usa IDs fixos e previsiveis para controles Xbox 360:
    //   XUSB: USB\VID_045E&PID_028E\01, \02, \03...
    //   HID:  HID\VID_045E&PID_028E&IG_XX\... (sufixo aleatorio, nao previsivel)
    //
    // Estrategia:
    //   1. Pre-gerar todos os XUSBs possiveis (\01 a \08) e injetar na blacklist.
    //      O device XUSB nasce JA BLOQUEADO (0ms de janela).
    //   2. Varredura proativa com SetupDi: acha HIDs ViGEm ja presentes e bloqueia.
    //   3. Callbacks + CM_Disable permanecem como rede de seguranca.

    void PreemptiveViGEmBlacklist() {
        if (!_hidHideAvailable) {
            Log("Preemptive: HidHide indisponivel — pulando pre-geracao.");
            return;
        }

        var preemptiveIds = new List<string>();

        // 1. XUSB previsiveis — \01 a \08 para margem
        // O ViGEmBus reutiliza estes suffixos fixos a cada conexao.
        // Cobrimos TODOS os modos de emulacao: Xbox 360, Xbox One/Series, DS4, DualSense.
        string[] xusbVidsPids = new string[] {
            // Microsoft Xbox 360
            "VID_045E&PID_028E",
            // Microsoft Xbox One (USB e Bluetooth)
            "VID_045E&PID_02FF",
            "VID_045E&PID_02EA",
            // Microsoft Xbox Series X|S
            "VID_045E&PID_0B12",
            "VID_045E&PID_0B13",
            // Sony DualShock 4
            "VID_054C&PID_05C4",
            "VID_054C&PID_09CC",
            // Sony DualSense (PS5)
            "VID_054C&PID_0CE6",
            "VID_054C&PID_0DF2",
        };
        foreach (var vidpid in xusbVidsPids) {
            for (int i = 1; i <= 8; i++) {
                preemptiveIds.Add("USB\\" + vidpid + "\\" + i.ToString("D2"));
            }
        }

        // 2. HID: varredura proativa — acha HIDs ViGEm ja presentes no sistema
        // e os adiciona a lista preemptiva. O sufixo do HID eh aleatorio,
        // entao nao podemos pre-gerar — apenas detectar os que ja existem.
        var guid = HID_GUID;
        IntPtr devs = SetupDiGetClassDevs(
            ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devs != new IntPtr(-1)) {
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
                    if (IsViGEmDevice(devInfo.DevInst)) {
                        string instanceId = SymLinkToInstanceId(path);
                        preemptiveIds.Add(instanceId);
                        Log("Preemptive: HID ViGEm detectado proactivemente (" + instanceId + ")");
                    }
                }
            } finally { SetupDiDestroyDeviceInfoList(devs); }
        }

        // 3. Injeta todos os IDs pre-calculados na blacklist do HidHide
        if (preemptiveIds.Count > 0) {
            HidHideNative.HideDevices(preemptiveIds);
            lock (_lock) {
                foreach (var id in preemptiveIds) _hiddenDevices.Add(id);
            }
            Log("Preemptive: " + preemptiveIds.Count + " ID(s) pre-bloqueado(s) antes de qualquer conexao.");
        }
    }

    void LoadPersistentXusbBlacklist() {
        try {
            if (!File.Exists(XUSB_PERSIST_PATH)) {
                Log("XUSB persist: arquivo nao existe (primeira execucao).");
                return;
            }
            var lines = File.ReadAllLines(XUSB_PERSIST_PATH);
            int loaded = 0;
            lock (_lock) {
                foreach (var line in lines) {
                    string id = line.Trim();
                    if (string.IsNullOrEmpty(id)) continue;
                    if (id.StartsWith("#")) continue; // comentario
                    _persistentXusbBlacklist.Add(id);
                    loaded++;
                }
            }
            if (loaded > 0 && _hidHideAvailable) {
                // Pre-popula a blacklist do HidHide ANTES de qualquer callback.
                // Isso garante que se o ViGEmBus recriar um XUSB com ID ja
                // conhecido, ele ja esta bloqueado antes de existir fisicamente.
                HidHideNative.HideDevices(new List<string>(_persistentXusbBlacklist));
                lock (_lock) {
                    foreach (var id in _persistentXusbBlacklist) _hiddenDevices.Add(id);
                }
                Log("XUSB persist: " + loaded + " ID(s) carregado(s) e pre-bloqueado(s) no HidHide.");
            } else {
                Log("XUSB persist: " + loaded + " ID(s) carregado(s) (HidHide indisponivel, apenas memoria).");
            }
        } catch (Exception ex) { Log("XUSB persist load erro: " + ex.Message); }
    }

    void SavePersistentXusbBlacklist() {
        try {
            Directory.CreateDirectory(XUSB_PERSIST_DIR);
            var sb = new StringBuilder();
            sb.AppendLine("# DuoFix XUSB Persistent Blacklist");
            sb.AppendLine("# Gerado automaticamente — NAO edite manualmente");
            sb.AppendLine("# " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            lock (_lock) {
                foreach (var id in _persistentXusbBlacklist)
                    sb.AppendLine(id);
            }
            File.WriteAllText(XUSB_PERSIST_PATH, sb.ToString());
        } catch (Exception ex) { Log("XUSB persist save erro: " + ex.Message); }
    }

    void AddToPersistentXusbBlacklist(string instanceId) {
        bool added;
        lock (_lock) { added = _persistentXusbBlacklist.Add(instanceId); }
        if (added) {
            SavePersistentXusbBlacklist();
            Log("XUSB persist: ID adicionado (" + instanceId + ")");
        }
    }

    void Stop2() {
        _running = false;
        if (_hNotify != IntPtr.Zero) {
            CM_Unregister_Notification(_hNotify);
            _hNotify = IntPtr.Zero;
        }
        if (_hNotifyXusb != IntPtr.Zero) {
            CM_Unregister_Notification(_hNotifyXusb);
            _hNotifyXusb = IntPtr.Zero;
        }
        if (_pollingThread != null) _pollingThread.Join(5000);
        if (_hidHideThread  != null) _hidHideThread.Join(3000);
        if (_watchdogThread != null) _watchdogThread.Join(2000);
        ShutdownHidHide();

        // Reabilita todos os XUSBs desabilitados via CM_Disable — reversibilidade total.
        // O controle volta a funcionar normalmente para o host apos o servico parar.
        uint[] toReEnable;
        lock (_lock) {
            toReEnable = new uint[_disabledXusbDevices.Count];
            _disabledXusbDevices.CopyTo(toReEnable);
            _disabledXusbDevices.Clear();
        }
        foreach (var devInst in toReEnable) {
            int rc = CM_Enable_DevNode(devInst, 0);
            Log("XUSB reabilitado no shutdown (devInst=" + devInst + ", rc=" + rc + ")");
        }

        Log("Parado.");
    }

    // =====================================================================
    // HidHide — Isolamento XInput via IOCTL direto
    // =====================================================================
    // Motivo: o HidHideCLI abre handle exclusivo ao device \\.\ HidHide.
    // Se outro processo (HidHideWatchdog, Duo Manager, ou uma instância
    // anterior do CLI) já tiver o handle, o CLI trava indefinidamente.
    // Usando DeviceIoControl direto com FILE_SHARE flags, evitamos deadlocks.

    // =====================================================================
    // HidHide — TODAS as operacoes via HidHideCLI.exe (sem IOCTL direto)
    // =====================================================================
    // Motivo: o driver \.\HidHide so permite UM handle por vez.
    // Misturar IOCTL direto com chamadas ao HidHideCLI.exe causa race condition
    // e falhas "handle falhou". O CLI tem retry interno e eh mais robusto.

    void InitHidHide() {
        if (!HidHideNative.IsAvailable) {
            Log("HidHide: driver nao encontrado — isolamento XInput desabilitado.");
            _hidHideAvailable = false;
            return;
        }
        // Abre handle persistente para o driver (zero latencia)
        if (!HidHideNative.Open()) {
            Log("HidHide: falha ao abrir handle persistente — isolamento XInput desabilitado.");
            _hidHideAvailable = false;
            return;
        }
        _hidHideAvailable = true;
        Log("HidHide: handle persistente aberto (IOCTL nativo, ~0.1ms).");

        // Limpa blacklist de execucoes anteriores (crash, servico morto no meio, etc.)
        try {
            var stale = HidHideNative.GetHiddenDevices();
            if (stale.Count > 0) {
                HidHideNative.UnhideDevices(stale);
                Log("HidHide: " + stale.Count + " entrada(s) obsoleta(s) removida(s) da blacklist.");
            }
        } catch (Exception ex) { Log("HidHide init limpeza erro: " + ex.Message); }

        // Ativar cloak
        HidHideNative.SetCloak(true);
        Log("HidHide: cloak ativado (XInput bloqueado para processos nao-whitelistados).");

        // Whitelist do Apollo/Sunshine — precisa sempre ver o device
        string sun1 = @"C:\Program Files\Duo\sunshine.exe";
        string sun2 = @"C:\Program Files\Apollo\sunshine.exe";
        if (File.Exists(sun1)) HidHideNative.AddToWhitelist(sun1);
        else if (File.Exists(sun2)) HidHideNative.AddToWhitelist(sun2);

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

        // Limpa blacklist de HID/USB, mas PRESERVA XUSBs persistentes.
        // Os XUSBs devem permanecer bloqueados mesmo entre reinicios do servico.
        try {
            var antes = HidHideNative.GetHiddenDevices();
            var toRemove = new List<string>();
            lock (_lock) {
                foreach (var id in antes) {
                    // Preserva XUSBs na blacklist persistente
                    if (_persistentXusbBlacklist.Contains(id)) continue;
                    toRemove.Add(id);
                }
            }
            if (toRemove.Count > 0) {
                HidHideNative.UnhideDevices(toRemove);
                Log("HidHide: blacklist limpa (" + toRemove.Count + " HID/USB removidos, " + (antes.Count - toRemove.Count) + " XUSB preservados).");
            } else if (antes.Count > 0) {
                Log("HidHide: blacklist mantida (" + antes.Count + " XUSBs persistentes preservados).");
            }
        } catch (Exception ex) { Log("HidHide shutdown blacklist erro: " + ex.Message); }

        lock (_lock) {
            _hiddenDevices.RemoveWhere(id => !_persistentXusbBlacklist.Contains(id));
        }

        // Desativa cloak
        HidHideNative.SetCloak(false);
        Log("HidHide: cloak desativado.");

        // Fecha handle persistente
        HidHideNative.Close();
        Log("HidHide: handle persistente fechado.");
    }

    // ---- Wrappers CLI (sem IOCTL) ----

    void HidHideAddToBlacklist(string instanceId) {
        if (!_hidHideAvailable) return;
        HidHideNative.HideDevice(instanceId);
    }

    void HidHideRemoveFromBlacklist(string instanceId) {
        if (!_hidHideAvailable) return;
        HidHideNative.UnhideDevice(instanceId);
    }

    void HidHideAddToWhitelist(string exePath) {
        if (!_hidHideAvailable || !File.Exists(exePath)) return;
        HidHideNative.AddToWhitelist(exePath);
        Log("HidHide whitelist +: " + exePath);
    }

    void HidHideRemoveFromWhitelist(string exePath) {
        if (!_hidHideAvailable) return;
        HidHideNative.RemoveFromWhitelist(exePath);
        Log("HidHide whitelist -: " + exePath);
    }

    // Monitora processos nas sessoes RDP (nao-console) e mantem a whitelist sincronizada.
    // IMPORTANTE: paths que tambem rodam na sessao console (ex: steam.exe do admin) sao
    // excluidos da whitelist para evitar que o host veja o controle virtual pelo mesmo exe.
    void HidHideWhitelistLoop() {
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Paths que NUNCA devem ser removidos da whitelist (processos criticos do streaming)
        var neverRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            @"C:\Program Files\Duo\sunshine.exe",
            @"C:\Program Files\Apollo\sunshine.exe"
        };
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
                foreach (var path in tracked) {
                    if (neverRemove.Contains(path)) continue; // NUNCA remover processos criticos
                    if (!rdpPaths.Contains(path) || consolePaths.Contains(path)) toRemove.Add(path);
                }
                foreach (var path in toRemove) {
                    HidHideRemoveFromWhitelist(path);
                    tracked.Remove(path);
                }
            } catch (Exception ex) { Log("HidHide whitelist loop erro: " + ex.Message); }
        }
    }

    // =====================================================================
    // Watchdog HidHide — detecta e corrige vazamento pos-Home button
    // =====================================================================
    // Problema: ao pressionar Home, o controle desliga e religa com novo
    // InstanceId. Durante a reconexao, fica visivel para todos ate o callback
    // CM_Register_Notification processar. Esse watchdog roda a cada 200ms
    // e forca a blacklist via IOCTL nativo antes que o host perceba.

    // Lista dispositivos HID gaming REALMENTE conectados via SetupDi.
    // NAO use GetHiddenDevices() como proxy — isso torna o watchdog cego.
    List<string> GetActiveGamingDevices() {
        var result = new List<string>();
        var guid = HID_GUID;
        IntPtr devs = SetupDiGetClassDevs(
            ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devs == new IntPtr(-1)) return result;
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
                string instanceId = SymLinkToInstanceId(path);
                if (string.IsNullOrEmpty(instanceId)) continue;

                // Target fisico (DS4/DualSense/Xbox redirecionado via RDP)?
                bool isTarget = false;
                for (int j = 0; j < PHYSICAL_BLOCK_VIDPIDS.Length; j++)
                    if (instanceId.IndexOf(PHYSICAL_BLOCK_VIDPIDS[j], StringComparison.OrdinalIgnoreCase) >= 0)
                        { isTarget = true; break; }

                // Target virtual (ViGEmBus)?
                if (!isTarget && IsViGEmDevice(devInfo.DevInst))
                    isTarget = true;

                if (isTarget)
                    result.Add(instanceId);
            }
        } finally { SetupDiDestroyDeviceInfoList(devs); }
        return result;
    }

    // Loga diagnostico completo do HidHide para debug do Home button.
    // Chamado quando o watchdog detecta vazamento ou a cada N ciclos.
    void LogHidHideDiagnostics(string reason) {
        try {
            bool cloak = HidHideNative.GetCloakState();
            var apps = HidHideNative.GetWhitelistedApps();
            var hidden = HidHideNative.GetHiddenDevices();
            Log("DIAG_HIDHIDE [" + reason + "]: cloak=" + cloak + " | hidden=" + hidden.Count + " | whitelisted_apps=" + apps.Count);
            foreach (var app in apps)
                Log("  DIAG_APP: " + app);
        } catch (Exception ex) { Log("DIAG_HIDHIDE erro: " + ex.Message); }
    }

    void HidHideWatchdogLoop() {
        int cycle = 0;
        bool lastCloakState = true;
        while (_running) {
            Thread.Sleep(50); // 50ms polling — deteccao rapida de vazamentos
            if (!_running) break;
            try {
                // 0. Processa pending unhide expirados (delayed unhide de 30s)
                // IMPORTANTE: so remove da blacklist se o device NAO existir mais.
                // O reciclo (CM_Disable/Enable) causa um REMOVAL transiente que agenda
                // pending unhide, mas o device e recriado imediatamente. Se executarmos
                // o unhide enquanto o device ainda existe, ele fica visivel para a Steam.
                var toUnhide = new List<string>();
                var toKeepHidden = new List<string>();
                lock (_lock) {
                    var now = DateTime.Now;
                    foreach (var kv in _pendingUnhide) {
                        if (now >= kv.Value) {
                            if (IsDevicePresent(kv.Key)) {
                                toKeepHidden.Add(kv.Key);
                            } else {
                                toUnhide.Add(kv.Key);
                            }
                        }
                    }
                    foreach (var id in toUnhide) {
                        _pendingUnhide.Remove(id);
                        _hiddenDevices.Remove(id);
                    }
                    foreach (var id in toKeepHidden) {
                        // Reseta o timer para +30s — tenta novamente depois
                        _pendingUnhide[id] = DateTime.Now.AddSeconds(PENDING_UNHIDE_SECONDS);
                    }
                }
                foreach (var id in toUnhide) {
                    HidHideRemoveFromBlacklist(id);
                    Log("  HidHide: delayed unhide executado (" + id + ")");
                }
                foreach (var id in toKeepHidden) {
                    Log("  HidHide: delayed unhide ADIADO (device ainda presente: " + id + ")");
                }

                // 1. Lista devices gaming REALMENTE conectados via SetupDi (nao use hidden como proxy)
                var gaming = GetActiveGamingDevices();
                if (gaming == null || gaming.Count == 0) continue;

                // 2. Lista devices ja escondidos
                var hidden = HidHideNative.GetHiddenDevices();
                var hiddenSet = new HashSet<string>(hidden, StringComparer.OrdinalIgnoreCase);

                // 3. Detecta mudanca no cloak (ex: Steam desativou globalmente)
                bool currentCloak = HidHideNative.GetCloakState();
                if (currentCloak != lastCloakState) {
                    Log("ALERTA_HIDHIDE: cloak mudou de " + lastCloakState + " para " + currentCloak + " — possivel interferencia externa (Steam/DS4Windows/etc).");
                    LogHidHideDiagnostics("cloak_changed");
                    lastCloakState = currentCloak;
                }

                // 4. Detecta targets que vazaram (deveriam estar escondidos mas nao estao)
                var leaked = new List<string>();
                foreach (var instanceId in gaming) {
                    // Target fisico (DS4/DualSense/Xbox redirecionado via RDP)?
                    bool isPhysicalTarget = false;
                    for (int j = 0; j < PHYSICAL_BLOCK_VIDPIDS.Length; j++)
                        if (instanceId.IndexOf(PHYSICAL_BLOCK_VIDPIDS[j], StringComparison.OrdinalIgnoreCase) >= 0)
                            { isPhysicalTarget = true; break; }

                    // Target virtual (ViGEmBus) que ja foi processado antes?
                    bool isVirtualTarget = false;
                    lock (_lock) {
                        isVirtualTarget = _hiddenDevices.Contains(instanceId) || _recyclingDevices.Contains(instanceId) || _pendingUnhide.ContainsKey(instanceId);
                    }

                    if ((isPhysicalTarget || isVirtualTarget) && !hiddenSet.Contains(instanceId))
                        leaked.Add(instanceId);
                }

                if (leaked.Count > 0) {
                    LogHidHideDiagnostics("pre_fix_leak");
                    HidHideNative.HideDevices(leaked);
                    foreach (var id in leaked)
                        Log("HidHide WATCHDOG: device vazado re-escondido (" + id + ")");
                    LogHidHideDiagnostics("post_fix_leak");
                }

                if (cycle == 0) Log("HidHide watchdog: ciclo 0 — " + gaming.Count + " gaming, " + hidden.Count + " hidden, " + leaked.Count + " leaked, " + _pendingUnhide.Count + " pending.");
                // A cada ~5 minutos (6000 ciclos * 50ms)
                if (cycle > 0 && cycle % 6000 == 0) LogHidHideDiagnostics("periodic_5min");
                cycle++;
            } catch (Exception ex) { Log("HidHide watchdog erro: " + ex.Message); }
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

    // Forca re-enumeracao de dispositivos HID em todos os aplicativos (Steam, etc.)
    static void NotifyDeviceChange() {
        try {
            uint recipients = BSM_APPLICATIONS;
            BroadcastSystemMessage(BSF_POSTMESSAGE, ref recipients, (uint)WM_DEVICECHANGE, (IntPtr)DBT_DEVNODES_CHANGED, IntPtr.Zero);
        } catch { }
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
                bool doUnhide = false;

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

                    // FISICO: nunca remove da blacklist (mantém bloqueio permanente)
                    if (_physicalBlockedDevices.Contains(instanceId)) {
                        Log("FISICO: REMOVAL ignorado — mantendo bloqueio (" + instanceId + ")");
                        return 0;
                    }

                    // VIRTUAL: delayed unhide — mantém na blacklist por 30s para absorver
                    // reconexões rápidas do Moonlight. Se o device for recriado nesse
                    // período, o ARRIVAL cancela o pending unhide.
                    if (_hidHideAvailable && !_recyclingDevices.Contains(instanceId)) {
                        if (_hiddenDevices.Contains(instanceId)) {
                            _pendingUnhide[instanceId] = DateTime.Now.AddSeconds(PENDING_UNHIDE_SECONDS);
                            Log("  HidHide: HID agendado para unhide em " + PENDING_UNHIDE_SECONDS + "s (" + instanceId + ")");
                        }

                        // Também agenda unhide do pai USB
                        string usbId = GetDeviceId(parent);
                        if (!string.IsNullOrEmpty(usbId) && !_recyclingDevices.Contains(usbId) && _hiddenDevices.Contains(usbId)) {
                            _pendingUnhide[usbId] = DateTime.Now.AddSeconds(PENDING_UNHIDE_SECONDS);
                            Log("  HidHide: USB pai agendado para unhide em " + PENDING_UNHIDE_SECONDS + "s (" + usbId + ")");
                        }
                    } else if (!_hidHideAvailable) {
                        // HidHide indisponível: remove imediatamente (não temos como proteger)
                        doUnhide = _hiddenDevices.Remove(instanceId);
                    }
                }

                if (doUnhide) {
                    HidHideRemoveFromBlacklist(instanceId);
                    Log("  HidHide: HID restaurado (HidHide indisponivel) (" + instanceId + ")");
                }
                return 0;
            }

            if (Action != CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL) return 0;

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

            // IMPORTANTE: verificar ViGEm ANTES de physical block.
            // O ViGEmBus emula controles Xbox (VID_045E&PID_028E) e DS4 (VID_054C&PID_05C4).
            // Se verificarmos physical primeiro, o virtual seria tratado como fisico — sem
            // CM_Disable preemptivo, sem reciclo, e sem delayed unhide. Isso deixa a
            // janela de race condition aberta para a Steam.
            bool isVigem = IsViGEmDevice(devInst);
            bool isPhysical = IsPhysicalBlockTarget(symLink);

            if (isVigem) {
                // Virtual ViGEm: processa com protecao completa (CM_Disable + DACL + reciclo + delayed unhide)
            } else if (isPhysical) {
                // Fisico real redirecionado via RDP: bloqueia via HidHide apenas
                ProcessPhysicalBlockDevice(symLink);
                lock (_lock) { _done.Add(symLink); }
                return 0;
            } else {
                // Nao e ViGEm nem fisico alvo: ignora
                return 0;
            }

            uint busDevInst = GetViGEmBusChild(devInst);

            // Protecao contra loop infinito: se processamos este symLink nos ultimos 10s,
            // apenas re-aplica HidHide e ignora o resto.
            lock (_lock) {
                DateTime lastProcessed;
                if (_recentlyProcessed.TryGetValue(symLink, out lastProcessed) && DateTime.Now < lastProcessed.AddSeconds(10)) {
                    Log("  ARRIVAL: symLink processado ha menos de 10s — ignorando loop.");
                    return 0;
                }
            }

            // Cancela delayed unhide se este device estava agendado para sair da blacklist.
            // Isso acontece em reconexões rápidas do Moonlight: o device é removido e
            // recriado dentro dos 30s do pending unhide.
            string arrivalInstanceId = SymLinkToInstanceId(symLink);
            bool cancelledPending = false;
            lock (_lock) {
                if (_pendingUnhide.Remove(arrivalInstanceId)) {
                    cancelledPending = true;
                    Log("  ARRIVAL: cancelado pending unhide para " + arrivalInstanceId);
                }
            }
            if (cancelledPending) {
                // Reconfirma na blacklist caso tenha sido removido pelo watchdog
                HidHideAddToBlacklist(arrivalInstanceId);
            }

            // === ABORDAGEM 3: CM_Disable PREEMPTIVO ===
            // Desabilita o device no kernel ANTES da Steam ou qualquer app
            // ter chance de abrir um handle. Isso acontece em ~1ms.
            // Tenta devInst (HID interface) primeiro — menos provavel de ser vetado que o USB pai.
            int rcDis1 = CM_Disable_DevNode(devInst, 0);
            if (rcDis1 != 0) {
                int rcDis2 = CM_Disable_DevNode(busDevInst, 0);
                Log("  CM_Disable: HID=" + rcDis1 + " USB=" + rcDis2 + " (0=OK)");
            } else {
                Log("  CM_Disable: HID OK (0)");
            }

            ProcessDevice(symLink, devInst, busDevInst);
        } catch (Exception ex) { Log("ERRO callback: " + ex.Message); }
        return 0;
    }

    // =====================================================================
    // Callback XUSB — bloqueio preemptivo do device XInput
    // =====================================================================
    int OnXusbEvent(IntPtr hNotify, IntPtr Context, int Action, IntPtr EventData, int EventDataSize) {
        try {
            if (EventData == IntPtr.Zero) return 0;
            string symLink = Marshal.PtrToStringUni(new IntPtr(EventData.ToInt64() + 24));
            if (string.IsNullOrEmpty(symLink)) return 0;

            // XUSB ARRIVAL: bloqueia na blacklist + desabilita no kernel
            if (Action == CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL) {
                string instanceId = SymLinkToInstanceId(symLink);
                // Verifica se ja foi processado recentemente (anti-loop)
                lock (_lock) {
                    DateTime lastProcessed;
                    if (_recentlyProcessed.TryGetValue(symLink, out lastProcessed) && DateTime.Now < lastProcessed.AddSeconds(10)) {
                        return 0;
                    }
                    _recentlyProcessed[symLink] = DateTime.Now;
                }

                // 1. HidHide blacklist (bloqueio de aplicacao)
                if (_hidHideAvailable) {
                    lock (_lock) { _hiddenDevices.Add(instanceId); }
                    HidHideAddToBlacklist(instanceId);
                    AddToPersistentXusbBlacklist(instanceId);
                }

                // 2. CM_Disable no kernel (bloqueio de kernel — impede XInput polling)
                // Isso desabilita o device antes da Steam ter chance de detectar.
                string xusbInstancePath = SymLinkToInstancePath(symLink);
                uint xusbDevInst;
                if (CM_Locate_DevNodeW(out xusbDevInst, xusbInstancePath, 0) == 0) {
                    int rcDis = CM_Disable_DevNode(xusbDevInst, 0);
                    lock (_lock) { _disabledXusbDevices.Add(xusbDevInst); }
                    Log("XUSB ARRIVAL: XInput bloqueado (HidHide+CM_Disable, devInst=" + xusbDevInst + ", rc=" + rcDis + ")");
                    
                    if (rcDis != 0) {
                        lock (_lock) { _recentlyProcessed.Remove(symLink); } // Permite nova tentativa imediata após reciclo
                    }
                } else {
                    Log("XUSB ARRIVAL: XInput bloqueado (HidHide apenas, devInst nao localizado)");
                    lock (_lock) { _recentlyProcessed.Remove(symLink); }
                }
            }
            // XUSB REMOVAL: NUNCA remove da blacklist.
            // O XUSB do ViGEmBus usa InstanceId FIXO (ex: USB\VID_045E&PID_028E\01).
            // Se mantivermos na blacklist permanentemente, quando o Moonlight reconecta
            // e recria o XUSB com o mesmo ID, ele ja esta bloqueado antes do callback.
            // O HidHide ignora IDs inexistentes na lista sem problemas.
            else if (Action == CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL) {
                string instanceId = SymLinkToInstanceId(symLink);
                lock (_lock) { _done.Remove(symLink); }
                Log("XUSB REMOVAL: XInput MANTIDO na blacklist permanentemente (" + instanceId + ")");
            }
        } catch (Exception ex) { Log("ERRO XUSB callback: " + ex.Message); }
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
        if (devs == new IntPtr(-1)) { Log("  Scan HID: SetupDiGetClassDevs falhou."); return; }
        int countAll = 0, countVigem = 0, countProcessed = 0, countNullPath = 0;
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
                if (path == null) { countNullPath++; continue; }
                countAll++;
                Log("  Scan HID enum: " + path + " | devInst=" + devInfo.DevInst);

                lock (_lock) { if (_done.Contains(path)) continue; }

                // Dispositivo fisico alvo (DS4/DualSense/Xbox via RDP)? Bloqueia direto.
                if (IsPhysicalBlockTarget(path)) {
                    ProcessPhysicalBlockDevice(path);
                    lock (_lock) { _done.Add(path); }
                    countProcessed++;
                    continue;
                }

                bool isVigem = IsViGEmDevice(devInfo.DevInst);
                Log("  Scan HID: " + path + " | IsViGEmDevice=" + isVigem);
                if (!isVigem) continue;
                countVigem++;

                uint busDevInst = GetViGEmBusChild(devInfo.DevInst);
                Log("  Scan encontrou device ativo: " + path);
                ProcessDevice(path, devInfo.DevInst, busDevInst);
                countProcessed++;
            }
            Log("  Scan HID: " + countAll + " presentes, " + countNullPath + " nullPath, " + countVigem + " ViGEm, " + countProcessed + " processados novos.");
        } finally { SetupDiDestroyDeviceInfoList(devs); }
    }

    string GetDevicePath(IntPtr devs, ref SP_DEVICE_INTERFACE_DATA iface, ref SP_DEVINFO_DATA devInfo) {
        uint needed = 0;
        var tmp = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
        SetupDiGetDeviceInterfaceDetail(devs, ref iface, IntPtr.Zero, 0, out needed, ref tmp);
        if (needed < 8) return null;

        IntPtr buf = Marshal.AllocHGlobal((int)needed);
        try {
            // x64 nativo: cbSize=8 (DWORD 4 + padding 4). x86: cbSize=6 (DWORD 4 + WCHAR 2).
            int cbSize = IntPtr.Size == 8 ? 8 : 6;
            Marshal.WriteInt32(buf, cbSize);
            if (!SetupDiGetDeviceInterfaceDetail(devs, ref iface, buf, needed, out needed, ref devInfo))
                return null;
            return Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
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

        // Bloqueio 2b: XUSB/XInput — o ViGEmBus cria um device XInput separado
        // que a Steam detecta. Precisamos bloquear ele tambem.
        string xusbId = null;
        var xusbInfo = FindXusbDevice(instanceId);
        if (xusbInfo != null) {
            xusbId = xusbInfo.Item1;
            string xusbPath = xusbInfo.Item2;
            
            // Bloqueio DACL ao vivo no XUSB (antes do reciclo)
            bool okXusb = ApplyDacl(xusbPath, consoleSid);
            Log("  DACL XUSB (ao vivo): " + (okXusb ? "OK" : "falhou"));
            
            lock (_lock) { _hiddenDevices.Add(xusbId); }
            HidHideAddToBlacklist(xusbId);
            Log("  HidHide: XUSB/XInput ocultado (" + xusbId + ")");
        }

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
            if (!string.IsNullOrEmpty(xusbId)) {
                WriteDeviceSecurityToRegistry(xusbId, consoleSid);
                Log("  WriteRegSec: DACL persistida no registro do XUSB (" + xusbId + ")");
            }

            // Marca devices como "em reciclo" para que a REMOVAL transiente
            // não os remova da blacklist do HidHide
            lock (_lock) { 
                _recyclingDevices.Add(instanceId); 
                if (!string.IsNullOrEmpty(usbParentId)) _recyclingDevices.Add(usbParentId);
            }

            // Verifica se este device ja foi processado recentemente (< 60s).
            // Se for primeira vez (device acabado de criar), nao ha handles abertos
            // para fechar — podemos usar o fast path (10ms) em vez do reciclo completo (400ms).
            bool isRecentReconnect = false;
            lock (_lock) {
                DateTime lastProcessed;
                if (_recentlyProcessed.TryGetValue(devicePath, out lastProcessed)) {
                    isRecentReconnect = DateTime.Now < lastProcessed.AddSeconds(60);
                }
            }

            if (isRecentReconnect) {
                Log("  Reciclo FAST (10ms): device processado ha menos de 60s — sem handles para fechar.");
            } else {
                Log("  Reciclo FULL: device novo ou inativo > 60s — fechando handles existentes...");
            }

            // Precisamos derrubar o busDevInst (pai) para forçar o filho XUSB a cair
            // e forçar o Windows a recarregar a DACL do XUSB do registro no Enable.
            
            uint xusbDevInst = 0;
            if (!string.IsNullOrEmpty(xusbId)) {
                CM_Locate_DevNodeW(out xusbDevInst, xusbId, 0); // Localiza o nó XUSB pelo ID
            }

            // Desabilita forçosamente o XUSB node caso ele tenha um handle aberto
            if (xusbDevInst != 0) {
                int rcXusbDis = (int)CM_Disable_DevNode(xusbDevInst, 0);
                Log("  Reciclo: Disable XUSB = " + rcXusbDis);
            }

            int rcDis = (int)CM_Disable_DevNode(busDevInst, 0);
            uint targetForEnable = busDevInst;
            if (rcDis != 0) {
                // Fallback: se o pai for vetado, tenta pelo menos o HID
                rcDis = (int)CM_Disable_DevNode(devInst, 0);
                targetForEnable = devInst;
            }
            // Fast path: 10ms se device acabado de criar; Full path: 50ms se reconexao
            Thread.Sleep(isRecentReconnect ? 10 : 50);
            
            if (xusbDevInst != 0) {
                CM_Enable_DevNode(xusbDevInst, 0);
            }
            
            int rcEna = (int)CM_Enable_DevNode(targetForEnable, 0);
            bool recycleOk = (rcDis == 0 && rcEna == 0);
            Log("  Reciclo: Disable=" + rcDis + " Enable=" + rcEna +
                (recycleOk ? " OK" : " AVISO — verifique privilegios") +
                (isRecentReconnect ? " (FAST)" : " (FULL)"));

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

            // Notifica todos os aplicativos (Steam, etc.) para reenumerar dispositivos.
            // Isso evita que apps ja abertos "cacheiem" o controle virtual antes do HidHide.
            NotifyDeviceChange();
            Log("  Notificacao WM_DEVICECHANGE enviada para reenumeracao.");

            // So re-enumeracao se o reciclo funcionou. Se falhou (veto), a re-enumeracao
            // pode causar cascata de callbacks REMOVAL/ARRIVAL que dispara loop infinito.
            if (recycleOk) {
                int rcReEnum = CM_Reenumerate_DevNode(busDevInst, CM_REENUMERATE_NORMAL);
                Log("  Re-enumeracao forçada do device (rc=" + rcReEnum + ").");
            } else {
                Log("  Re-enumeracao PULADA (reciclo falhou — evita loop infinito).");
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

        // Registra que este device foi processado agora (protecao contra loop infinito)
        lock (_lock) {
            _recentlyProcessed[devicePath] = DateTime.Now;
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

    // Procura um device XUSB/XInput correspondente ao HID ViGEm.
    // O ViGEmBus cria dois devices: um HID e um XUSB. A Steam detecta via XInput,
    // entao precisamos bloquear ambos.
    Tuple<string, string> FindXusbDevice(string hidInstanceId) {
        try {
            // Extrai VID e PID do instanceId HID
            int vidIdx = hidInstanceId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
            int pidIdx = hidInstanceId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
            if (vidIdx < 0 || pidIdx < 0) return null;
            string vid = hidInstanceId.Substring(vidIdx, 8); // VID_XXXX
            string pid = hidInstanceId.Substring(pidIdx, 8); // PID_XXXX

            var guid = XUSB_GUID;
            IntPtr devs = SetupDiGetClassDevs(
                ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (devs == new IntPtr(-1)) return null;
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
                    // Verifica se o path XUSB contem o mesmo VID/PID
                    if (path.IndexOf(vid, StringComparison.OrdinalIgnoreCase) >= 0 &&
                        path.IndexOf(pid, StringComparison.OrdinalIgnoreCase) >= 0) {
                        return new Tuple<string, string>(GetDeviceId(devInfo.DevInst), path);
                    }
                }
            } finally { SetupDiDestroyDeviceInfoList(devs); }
        } catch (Exception ex) { Log("  FindXusbDevice erro: " + ex.Message); }
        return null;
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

    bool IsDevicePresent(string instanceId) {
        try {
            uint devInst;
            return CM_Locate_DevNodeW(out devInst, instanceId, 0) == 0;
        } catch { return false; }
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
        string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + msg;
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

    // Rotação por tempo: mantém apenas linhas dos últimos N dias.
    // O formato esperado é [yyyy-MM-dd HH:mm:ss.fff] no início da linha.
    static void TruncateLogByTime(int days = 2) {
        try {
            if (!File.Exists(LOG_PATH)) return;
            string[] lines = File.ReadAllLines(LOG_PATH);
            if (lines.Length == 0) return;
            DateTime cutoff = DateTime.Now.AddDays(-days);
            var keep = new List<string>();
            foreach (var line in lines) {
                if (line.Length < 24) { keep.Add(line); continue; } // sem data, mantém por segurança
                if (line[0] != '[') { keep.Add(line); continue; }
                DateTime dt;
                if (DateTime.TryParseExact(line.Substring(1, 19), "yyyy-MM-dd HH:mm:ss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out dt)) {
                    if (dt >= cutoff) keep.Add(line);
                } else {
                    keep.Add(line); // não conseguiu parse, mantém
                }
            }
            if (keep.Count < lines.Length) {
                File.WriteAllLines(LOG_PATH, keep);
                Log("LOG_ROTATOR: " + (lines.Length - keep.Count) + " linhas antigas removidas (mantendo ultimos " + days + " dias).");
            }
        } catch { }
    }

    void LogRotatorLoop() {
        while (_running) {
            for (int i = 0; i < 3600 && _running; i++) Thread.Sleep(1000); // a cada 1h
            if (!_running) break;
            TruncateLogByTime(2);
        }
    }

    // =====================================================================
    // Instalação / remoção
    // =====================================================================

    static void Install() {
        string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
        // sc.exe exige aspas escapadas com \ quando o binPath tem espaços.
        // Separamos create, displayname e description em comandos distintos para evitar
        // parsing errors do sc.exe quando o path contém espaço.
        Exec("sc.exe", "create " + SERVICE_NAME + " binPath= \"\\\"" + exe + "\\\"\" start= auto");
        Exec("sc.exe", "config " + SERVICE_NAME + " DisplayName= \"Duo Gamepad Isolator\"");
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
