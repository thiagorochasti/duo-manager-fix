// HidHideCliHelper — Wrapper oficial para HidHideCLI.exe
//
// Todas as operacoes usam HidHideCLI.exe (com lock e retry) para evitar
// conflitos de handle com o driver \.\HidHide, que so permite uma conexao.
//
// Comandos suportados (HidHide v1.2+):
//   --dev-hide <instanceId>    Esconde device (blacklist)
//   --dev-unhide <instanceId>  Revela device
//   --dev-list                 Lista devices escondidos
//   --dev-gaming               Lista devices gaming
//   --dev-all                  Lista todos os HID devices
//   --cloak-on / --cloak-off   Ativa/desativa cloak
//   --app-reg <path>           Adiciona app na whitelist
//   --app-unreg <path>         Remove app da whitelist
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

static class HidHideCliHelper {

    static readonly string CLI_PATH = @"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe";
    static readonly object _lock = new object();

    static bool _available;
    static bool _checked;

    public static bool IsAvailable {
        get {
            if (!_checked) {
                _available = File.Exists(CLI_PATH);
                _checked = true;
            }
            return _available;
        }
    }

    // Executa um ou mais comandos sequenciados. Retorna stdout completo.
    // Usa lock para serializar acessos e retry com backoff para evitar
    // falhas quando outro processo esta usando o driver.
    static string Run(params string[] args) {
        if (!IsAvailable) return null;
        string arguments = string.Join(" ", args);
        lock (_lock) {
            for (int attempt = 0; attempt < 5; attempt++) {
                var psi = new ProcessStartInfo(CLI_PATH) {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    Arguments = arguments
                };
                try {
                    using (var proc = Process.Start(psi)) {
                        string stdout = proc.StandardOutput.ReadToEnd();
                        string stderr = proc.StandardError.ReadToEnd();
                        proc.WaitForExit(8000);
                        // Log detalhado de TODAS as chamadas para diagnostico
                        DebugLog("RUN [attempt=" + (attempt+1) + ", exit=" + proc.ExitCode + "] args=" + arguments);
                        if (!string.IsNullOrEmpty(stdout)) DebugLog("  stdout: " + stdout.Trim().Replace("\r\n", " | "));
                        if (!string.IsNullOrEmpty(stderr)) DebugLog("  stderr: " + stderr.Trim().Replace("\r\n", " | "));

                        if (proc.ExitCode == 0) return stdout;
                        // Se falhou por handle ocupado, tenta novamente
                        if (stderr.Contains("access") || stderr.Contains("denied") ||
                            stderr.Contains("busy") || stdout.Contains("busy")) {
                            Thread.Sleep(50 * (attempt + 1));
                            continue;
                        }
                        if (!string.IsNullOrEmpty(stderr))
                            DebugLog("HidHideCLI erro (tentativa " + (attempt+1) + "): " + stderr.Trim());
                        if (attempt == 4) return stdout; // retorna mesmo com erro na ultima tentativa
                    }
                } catch (Exception ex) {
                    DebugLog("HidHideCLI excecao (tentativa " + (attempt+1) + "): " + ex.Message);
                    Thread.Sleep(50 * (attempt + 1));
                }
            }
            return null;
        }
    }

    // =====================================================================
    // Operações individuais
    // =====================================================================

    public static void HideDevice(string instanceId) {
        if (string.IsNullOrEmpty(instanceId)) return;
        string id = NormalizeInstanceId(instanceId);
        Run("--dev-hide", Quote(id));
    }

    public static void UnhideDevice(string instanceId) {
        if (string.IsNullOrEmpty(instanceId)) return;
        string id = NormalizeInstanceId(instanceId);
        Run("--dev-unhide", Quote(id));
    }

    public static void SetCloak(bool active) {
        Run(active ? "--cloak-on" : "--cloak-off");
    }

    public static void AddToWhitelist(string exePath) {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;
        Run("--app-reg", Quote(exePath));
    }

    public static void RemoveFromWhitelist(string exePath) {
        if (string.IsNullOrEmpty(exePath)) return;
        Run("--app-unreg", Quote(exePath));
    }

    // =====================================================================
    // Batch: esconde / revela múltiplos devices numa única chamada ao CLI
    // =====================================================================

    public static void HideDevices(IEnumerable<string> instanceIds) {
        var args = new List<string>();
        foreach (var id in instanceIds) {
            if (!string.IsNullOrEmpty(id)) {
                args.Add("--dev-hide");
                args.Add(Quote(id));
            }
        }
        if (args.Count > 0) Run(args.ToArray());
    }

    public static void UnhideDevices(IEnumerable<string> instanceIds) {
        var args = new List<string>();
        foreach (var id in instanceIds) {
            if (!string.IsNullOrEmpty(id)) {
                args.Add("--dev-unhide");
                args.Add(Quote(id));
            }
        }
        if (args.Count > 0) Run(args.ToArray());
    }

    // Limpa TODOS os devices escondidos (blacklist)
    public static void ClearHiddenDevices() {
        var hidden = GetHiddenDevices();
        if (hidden.Count > 0) UnhideDevices(hidden);
    }

    // =====================================================================
    // Leitura de estado
    // =====================================================================

    public static List<string> GetHiddenDevices() {
        string output = Run("--dev-list");
        var list = ParseDeviceList(output);
        // Normaliza todos os IDs para garantir comparacao consistente
        for (int i = 0; i < list.Count; i++) list[i] = NormalizeInstanceId(list[i]);
        return list;
    }

    public static List<string> GetGamingDevices() {
        string output = Run("--dev-gaming");
        var list = ParseDeviceList(output);
        for (int i = 0; i < list.Count; i++) list[i] = NormalizeInstanceId(list[i]);
        return list;
    }

    public static List<string> GetAllDevices() {
        string output = Run("--dev-all");
        var list = ParseDeviceList(output);
        for (int i = 0; i < list.Count; i++) list[i] = NormalizeInstanceId(list[i]);
        return list;
    }

    public static bool GetCloakState() {
        string output = Run("--cloak-state");
        if (string.IsNullOrWhiteSpace(output)) return true; // assume ligado se nao conseguir ler
        string lower = output.ToLowerInvariant();
        if (lower.Contains("disabled") || lower.Contains("off")) return false;
        return true;
    }

    public static List<string> GetWhitelistedApps() {
        string output = Run("--app-list");
        return ParseAppList(output);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    static string Quote(string s) {
        if (s.Contains(" ")) return "\"" + s + "\"";
        return s;
    }

    static List<string> ParseDeviceList(string output) {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(output)) return result;
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("HidHide")) continue;
            if (trimmed.StartsWith("Copyright")) continue;
            if (trimmed.StartsWith("Usage")) continue;
            if (trimmed.StartsWith("The above")) continue;
            if (trimmed.StartsWith("Error")) continue;
            if (trimmed.StartsWith("CONFIGRET")) continue;
            if (trimmed.Length < 5) continue;

            // 1) HidHide v1.5+ --dev-list returns: --dev-hide "HID\VID_045E&PID_028E..."
            if (trimmed.StartsWith("--dev-hide ") || trimmed.StartsWith("--dev-unhide ")) {
                int firstQuote = trimmed.IndexOf('"');
                int lastQuote = trimmed.LastIndexOf('"');
                if (firstQuote >= 0 && lastQuote > firstQuote) {
                    string id = trimmed.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
                    if (IsValidInstanceId(id) && !result.Contains(id)) result.Add(id);
                }
                continue;
            }

            // 2) Extract ANY quoted string that looks like a valid device instance path.
            //    This handles JSON, partial JSON, or any other format the CLI may return.
            int quoteStart = trimmed.IndexOf('"');
            while (quoteStart >= 0) {
                int quoteEnd = trimmed.IndexOf('"', quoteStart + 1);
                if (quoteEnd < 0) break;
                string candidate = trimmed.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
                if (IsValidInstanceId(candidate) && !result.Contains(candidate))
                    result.Add(candidate);
                quoteStart = trimmed.IndexOf('"', quoteEnd + 1);
            }

            // 3) Fallback: any unquoted line that looks like a device instance path
            if (IsValidInstanceId(trimmed) && !result.Contains(trimmed))
                result.Add(trimmed);
        }
        return result;
    }

    static bool IsValidInstanceId(string s) {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (s.Length < 10) return false;
        return (s.StartsWith("HID\\") || s.StartsWith("USB\\")) && s.Contains("VID_");
    }

    // Normaliza InstanceId removendo escaping duplo de barras invertidas
    // que o HidHideCLI v1.5.230 as vezes retorna no --dev-gaming
    public static string NormalizeInstanceId(string s) {
        if (string.IsNullOrEmpty(s)) return s;
        // Remove escaping duplo: \\ → \
        return s.Replace("\\\\", "\\");
    }

    static List<string> ParseAppList(string output) {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(output)) return result;
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("--")) continue;
            if (trimmed.StartsWith("HidHide")) continue;
            if (trimmed.StartsWith("Copyright")) continue;
            if (trimmed.StartsWith("Usage")) continue;
            if (trimmed.StartsWith("The above")) continue;
            if (trimmed.Length < 3) continue;
            result.Add(trimmed);
        }
        return result;
    }

    static void DebugLog(string msg) {
        try {
            string log = @"C:\Users\Public\duo_hidhide_cli.log";
            File.AppendAllText(log, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + msg + Environment.NewLine);
        } catch { }
    }
}
