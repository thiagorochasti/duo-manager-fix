using System;
using System.Runtime.InteropServices;
using System.Text;
using System.ServiceProcess;
using Microsoft.Win32;

// Roda como SYSTEM (via serviço) para ler os Parameters do HidHide
class ReadHidHideParams : ServiceBase {
    static void Main(string[] args) {
        Console.WriteLine("=== HidHide Driver Parameters ===");
        Console.WriteLine("(Rodando como: " + System.Security.Principal.WindowsIdentity.GetCurrent().Name + ")");
        Console.WriteLine();
        
        try {
            var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\HidHide\Parameters");
            if (key == null) { Console.WriteLine("Key nao encontrada!"); return; }
            
            foreach (var name in key.GetValueNames()) {
                var kind = key.GetValueKind(name);
                var val = key.GetValue(name);
                Console.WriteLine(name + " (" + kind + "):");
                if (val is string[]) {
                    foreach (var s in (string[])val) Console.WriteLine("  " + s);
                } else if (val is byte[]) {
                    Console.WriteLine("  [" + ((byte[])val).Length + " bytes]");
                } else {
                    Console.WriteLine("  " + val);
                }
            }
            key.Close();
        } catch (Exception ex) {
            Console.WriteLine("ERRO: " + ex.Message);
        }
        
        // Testar ESCRITA
        Console.WriteLine();
        Console.WriteLine("=== Testando escrita ===");
        try {
            var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\HidHide\Parameters", true);
            if (key == null) {
                Console.WriteLine("Key nao abriu com write!");
                return;
            }
            
            // Ler blacklist atual
            var bl = key.GetValue("BlacklistedDeviceInstancePaths") as string[];
            Console.WriteLine("Blacklist atual: " + (bl != null ? bl.Length + " items" : "null"));
            if (bl != null) foreach (var s in bl) Console.WriteLine("  " + s);
            
            // Adicionar nosso device
            string dev = @"HID\VID_054C&PID_05C4&REV_0100\2&130C1E12&0&0000";
            var newBl = bl != null ? new string[bl.Length + 1] : new string[1];
            if (bl != null) bl.CopyTo(newBl, 0);
            newBl[newBl.Length - 1] = dev;
            
            key.SetValue("BlacklistedDeviceInstancePaths", newBl, RegistryValueKind.MultiString);
            Console.WriteLine("Blacklist escrita OK!");
            
            // Ler IsActive
            var active = key.GetValue("IsActive");
            Console.WriteLine("IsActive: " + active);
            
            // Setar IsActive = 1
            key.SetValue("IsActive", 1, RegistryValueKind.DWord);
            Console.WriteLine("IsActive setado para 1.");
            
            // Verificar
            bl = key.GetValue("BlacklistedDeviceInstancePaths") as string[];
            Console.WriteLine("Blacklist apos escrita: " + (bl != null ? bl.Length + " items" : "null"));
            if (bl != null) foreach (var s in bl) Console.WriteLine("  " + s);
            
            key.Close();
            Console.WriteLine("\nSUCESSO! Reinicie o driver HidHide para aplicar.");
        } catch (Exception ex) {
            Console.WriteLine("ERRO escrita: " + ex.Message);
        }
    }
}
