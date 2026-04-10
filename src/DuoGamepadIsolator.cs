// DuoGamepadIsolator — Isola controles virtuais ViGEmBus por sessão de streaming
//
// Lógica: quando um device ViGEmBus é detectado, aplica DACL:
//   DENY  [usuário da sessão console]   ← bloqueia o usuário principal
//   ALLOW EVERYONE                      ← permite Games, SYSTEM, etc.
// Depois faz CM_Disable/Enable para forçar fechamento de handles já abertos.
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

    // Janela de tempo (ms) em que arrivals após reciclo são ignorados
    // Evita loop infinito: disable→REMOVAL→enable→ARRIVAL→reprocessa→loop
    const int RECYCLE_WINDOW_MS = 4000;

    static readonly Guid HID_GUID = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");

    const string LOG_PATH     = @"C:\Users\Public\duo_isolator.log";
    const string SERVICE_NAME = "DuoGamepadIsolator";

    // =====================================================================
    // Estado
    // =====================================================================

    IntPtr             _hNotify = IntPtr.Zero;
    CM_NOTIFY_CALLBACK _callbackDelegate;
    Thread             _pollingThread;
    volatile bool      _running;
    readonly object    _lock = new object();
    uint               _vigemBusInst = 0;

    // _done: device paths já processados (DACL aplicado)
    readonly HashSet<string> _done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // _recycleUntil: timestamp até quando ignorar arrivals de reciclo.
    // Chave = devicePath. Evita o loop: DACL→disable→REMOVAL→enable→ARRIVAL→reprocessa.
    readonly Dictionary<string, DateTime> _recycleUntil =
        new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

    // _devInstProcessed: devInsts já processados nesta sessão.
    // Um mesmo devInst (bus parent) pode aparecer via múltiplas interfaces Col01/Col02...
    // Ao detectar o primeiro, recicla o device pai; as outras interfaces não precisam de reciclo extra.
    readonly HashSet<uint> _devInstProcessed = new HashSet<uint>();

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
        lock (_lock) {
            _done.Clear();
            _recycleUntil.Clear();
            _devInstProcessed.Clear();
        }
    }

    void InitialScan() {
        Thread.Sleep(1000);
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
        Log("Parado.");
    }

    // =====================================================================
    // Modo A: Kernel callback (CM_Register_Notification)
    // =====================================================================

    int OnDeviceEvent(IntPtr hNotify, IntPtr Context, int Action, IntPtr EventData, int EventDataSize) {
        try {
            if (EventData == IntPtr.Zero) return 0;
            // CM_NOTIFY_EVENT_DATA: FilterType(4) + Reserved(4) + ClassGuid(16) + SymbolicLink(var)
            string symLink = Marshal.PtrToStringUni(new IntPtr(EventData.ToInt64() + 24));
            if (string.IsNullOrEmpty(symLink)) return 0;

            if (Action == CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL) {
                lock (_lock) {
                    _done.Remove(symLink);
                    // Não limpa _recycleUntil aqui — ele expira naturalmente pelo timestamp.
                    // Não limpa _devInstProcessed — limpo apenas quando sunshine para (polling)
                    // ou no próximo Start().
                }
                return 0;
            }
            if (Action != CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL) return 0;

            // Verifica se está dentro da janela de reciclo (evita loop infinito)
            lock (_lock) {
                if (_done.Contains(symLink)) return 0;
                DateTime until;
                if (_recycleUntil.TryGetValue(symLink, out until) && DateTime.Now < until) {
                    _done.Add(symLink); // Re-adiciona ao done sem reprocessar
                    return 0;
                }
            }

            string instancePath = SymLinkToInstancePath(symLink);
            uint devInst;
            if (CM_Locate_DevNodeW(out devInst, instancePath, 0) != 0) return 0;
            if (!IsViGEmDevice(devInst)) return 0;

            // Obtém o devInst do bus pai (para reciclo e deduplicação de interfaces)
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
                // Sem sessão ativa: limpa todo o estado para próxima sessão
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

                lock (_lock) { if (_done.Contains(path)) continue; }
                if (!IsViGEmDevice(devInfo.DevInst)) continue;

                uint busDevInst = GetViGEmBusChild(devInfo.DevInst);
                ProcessDevice(path, devInfo.DevInst, busDevInst);
            }
        } finally { SetupDiDestroyDeviceInfoList(devs); }
    }

    string GetDevicePath(IntPtr devs, ref SP_DEVICE_INTERFACE_DATA iface, ref SP_DEVINFO_DATA devInfo) {
        uint needed = 0;
        // Primeira chamada com buffer nulo só para obter o tamanho necessário.
        // Usa cópia temporária de devInfo para não interferir com o caller.
        var tmp = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
        SetupDiGetDeviceInterfaceDetail(devs, ref iface, IntPtr.Zero, 0, out needed, ref tmp);
        if (needed < 5) return null;

        uint bufSize = needed + 32; // margem extra
        IntPtr buf = Marshal.AllocHGlobal((int)bufSize);
        try {
            // cbSize: 8 em 64-bit, 6 em 32-bit
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(devs, ref iface, buf, bufSize, out needed, ref devInfo))
                return null;
            // DevicePath começa no offset 4 (logo após o DWORD cbSize)
            string path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
            // Valida que é um caminho real de device
            if (path == null || !path.StartsWith(@"\\?\")) return null;
            return path;
        } finally { Marshal.FreeHGlobal(buf); }
    }

    // =====================================================================
    // Lógica principal: DACL + reciclo sem loop
    // =====================================================================

    void ProcessDevice(string devicePath, uint devInst, uint busDevInst) {
        SecurityIdentifier consoleSid = GetConsoleUserSid();

        // Deduplicação por busDevInst: múltiplas interfaces HID (Col01, Col02...)
        // do mesmo device virtual mapeiam para o mesmo busDevInst.
        // Apenas a primeira interface aplica o reciclo; as outras só aplicam DACL.
        bool isFirstInterface;
        lock (_lock) {
            isFirstInterface = !_devInstProcessed.Contains(busDevInst);
            if (isFirstInterface) _devInstProcessed.Add(busDevInst);
        }

        Log("ViGEmBus device: " + devicePath +
            (isFirstInterface ? "" : " [interface adicional, sem reciclo]"));

        if (consoleSid != null)
            Log("  DENY console SID: " + consoleSid.Value);
        else
            Log("  Sem usuario console — apenas ALLOW EVERYONE.");

        bool ok = ApplyDacl(devicePath, consoleSid);
        if (!ok) {
            Log("  Falha ao aplicar DACL.");
            return;
        }

        // Marca como processado antes do reciclo para capturar a janela
        lock (_lock) {
            _done.Add(devicePath);
            _recycleUntil[devicePath] = DateTime.Now.AddMilliseconds(RECYCLE_WINDOW_MS);
        }

        Log("  DACL OK (DENY console + ALLOW EVERYONE).");

        if (isFirstInterface) {
            // Recicla o device para invalidar handles já abertos (ex: Steam já tinha o handle)
            Log("  Reciclando device (disable+enable) para fechar handles existentes...");
            int rcDis = CM_Disable_DevNode(busDevInst, 0);
            Thread.Sleep(400);
            int rcEna = CM_Enable_DevNode(busDevInst, 0);
            Log("  Reciclo: Disable=" + rcDis + " Enable=" + rcEna +
                (rcDis == 0 && rcEna == 0 ? " OK" : " AVISO — verifique privilegios"));
        }
    }

    // Retorna o devInst do device filho imediato do ViGEmBus bus
    // (ancestral direto da HID interface que é filho do bus)
    uint GetViGEmBusChild(uint devInst) {
        if (_vigemBusInst == 0) return devInst;
        uint cur = devInst;
        uint prev = devInst;
        for (int i = 0; i < 5; i++) {
            uint parent;
            if (CM_Get_Parent(out parent, cur, 0) != 0) break;
            if (parent == _vigemBusInst) return cur; // cur é filho direto do bus
            prev = cur;
            cur  = parent;
        }
        return devInst; // fallback: usa o próprio devInst
    }

    // =====================================================================
    // Helpers
    // =====================================================================

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

    // DACL: DENY [consoleSid] + ALLOW EVERYONE
    // Se consoleSid == null, apenas ALLOW EVERYONE (sem usuário no console para bloquear)
    bool ApplyDacl(string devicePath, SecurityIdentifier consoleSid) {
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        int aclSize  = 8;
        if (consoleSid != null) aclSize += 4 + 4 + consoleSid.BinaryLength;
        aclSize += 4 + 4 + everyone.BinaryLength;

        IntPtr pAcl   = Marshal.AllocHGlobal(aclSize);
        var    pinned = new List<GCHandle>();
        try {
            if (!InitializeAcl(pAcl, (uint)aclSize, ACL_REVISION)) return false;

            // DENY primeiro (ACEs de deny são processados antes de allow)
            if (consoleSid != null) {
                byte[] b = new byte[consoleSid.BinaryLength];
                consoleSid.GetBinaryForm(b, 0);
                GCHandle h = GCHandle.Alloc(b, GCHandleType.Pinned);
                pinned.Add(h);
                AddAccessDeniedAce(pAcl, ACL_REVISION, GENERIC_ALL, h.AddrOfPinnedObject());
            }

            // ALLOW EVERYONE
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

    // =====================================================================
    // Log
    // =====================================================================

    static void Log(string msg) {
        string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + msg;
        Console.WriteLine(line);
        try { File.AppendAllText(LOG_PATH, line + Environment.NewLine); } catch { }
    }

    // Mantém apenas as últimas maxLines linhas do log (chamado no Start)
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
        Exec("sc.exe", "start " + SERVICE_NAME);
        Console.WriteLine("Servico instalado. Log: " + LOG_PATH);
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
