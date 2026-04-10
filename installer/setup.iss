; Duo Manager Fix — Inno Setup Script
; Requer Inno Setup 6.x: https://jrsoftware.org/isinfo.php
;
; Antes de compilar:
;   1. Execute scripts\build.bat          → gera bin\DuoRdpWrapper.exe e bin\DuoGamepadIsolator.exe
;   2. Execute scripts\prepare_bundle.bat → popula bundled\ com sunshine.exe e web assets do Apollo
;   3. Abra este arquivo no Inno Setup Compiler e pressione F9

#define AppName "Duo Manager Fix"
#define AppVersion "1.0.1"
#define AppPublisher "thiagorochasti"
#define AppURL "https://github.com/thiagorochasti/duo-manager-fix"
#define ServiceName "DuoGamepadIsolator"
#define DuoDir "C:\Program Files\Duo"
#define InstallDir "C:\Program Files\DuoFix"

[Setup]
AppId={{8F2A1C3E-4B7D-4E9F-A1C2-3D5E6F7A8B9C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
DefaultDirName={#InstallDir}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\release
OutputBaseFilename=DuoManagerFix-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
MinVersion=10.0
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\DuoGamepadIsolator.exe
CloseApplications=yes
AlwaysRestart=no

[Languages]
Name: "portuguese"; MessagesFile: "compiler:Languages\Portuguese.isl"
Name: "english";    MessagesFile: "compiler:Default.isl"

[Messages]
portuguese.WelcomeLabel1=Bem-vindo ao instalador do %1
portuguese.WelcomeLabel2=Este instalador corrige tres problemas do Duo Manager 1.5.6:%n%n  1. Resolucao travada em 640x480%n  2. Interface web de gerenciamento quebrada%n  3. Controles virtuais vazando entre sessoes%n%nPre-requisito: Duo Manager 1.5.6 instalado.%n%nClique em Avancar para continuar.
portuguese.FinishedLabel=Instalacao concluida!%n%nO servico DuoGamepadIsolator esta em execucao.%nConecte pelo Moonlight e teste.

[Files]
; Binarios compilados (scripts\build.bat)
Source: "..\bin\DuoRdpWrapper.exe";      DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "..\bin\DuoGamepadIsolator.exe"; DestDir: "{app}"; Flags: ignoreversion

; Apollo sunshine.exe (scripts\prepare_bundle.bat)
Source: "..\bundled\sunshine.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

; Web assets do Apollo
Source: "..\bundled\web\pin.html";     DestDir: "{tmp}\web"; Flags: deleteafterinstall
Source: "..\bundled\web\login.html";   DestDir: "{tmp}\web"; Flags: deleteafterinstall
Source: "..\bundled\web\welcome.html"; DestDir: "{tmp}\web"; Flags: deleteafterinstall
Source: "..\bundled\web\assets\*";     DestDir: "{tmp}\web\assets"; Flags: recursesubdirs deleteafterinstall

[Code]

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not FileExists(ExpandConstant('{#DuoDir}\Duo.exe')) then begin
    MsgBox(
      'Duo Manager nao encontrado em:' + #13#10 +
      '  {#DuoDir}' + #13#10 + #13#10 +
      'Instale o Duo Manager 1.5.6 antes de continuar.' + #13#10 +
      'Download: https://github.com/DuoStream/Duo/releases/tag/v1.5.6',
      mbCriticalError, MB_OK);
    Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  DuoDir: String;
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then Exit;

  DuoDir := ExpandConstant('{#DuoDir}');

  // --- Fix 1: DuoRdpWrapper (resolucao 4K) ---
  Exec('takeown.exe', '/f "' + DuoDir + '\DuoRdp.exe" /a',                   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('icacls.exe',  '"' + DuoDir + '\DuoRdp.exe" /grant Administrators:F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if not FileExists(DuoDir + '\DuoRdp_orig.exe') then
    FileCopy(DuoDir + '\DuoRdp.exe', DuoDir + '\DuoRdp_orig.exe', False);
  FileCopy(ExpandConstant('{tmp}\DuoRdpWrapper.exe'), DuoDir + '\DuoRdp.exe', True);

  // --- Fix 2: Apollo sunshine.exe (encoder RTX) ---
  Exec('takeown.exe', '/f "' + DuoDir + '\sunshine.exe" /a',                   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('icacls.exe',  '"' + DuoDir + '\sunshine.exe" /grant Administrators:F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if not FileExists(DuoDir + '\sunshine_orig.exe') then
    FileCopy(DuoDir + '\sunshine.exe', DuoDir + '\sunshine_orig.exe', False);
  FileCopy(ExpandConstant('{tmp}\sunshine.exe'), DuoDir + '\sunshine.exe', True);

  // --- Fix 3: Web assets (interface de gerenciamento) ---
  ForceDirectories(DuoDir + '\assets\web\assets');
  FileCopy(ExpandConstant('{tmp}\web\pin.html'),     DuoDir + '\assets\web\pin.html',     True);
  FileCopy(ExpandConstant('{tmp}\web\login.html'),   DuoDir + '\assets\web\login.html',   True);
  FileCopy(ExpandConstant('{tmp}\web\welcome.html'), DuoDir + '\assets\web\welcome.html', True);
  Exec('xcopy.exe',
    '"' + ExpandConstant('{tmp}\web\assets\*') + '" "' + DuoDir + '\assets\web\assets\" /Y /E /Q',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // --- Fix 4: Games_apps.json ---
  SaveStringToFile(
    DuoDir + '\config\Games_apps.json',
    '{"apps":[' +
      '{"uuid":"3C56B52A-50C8-3601-CC0D-042310A47F60","image-path":"desktop.png","name":"Desktop","prep-cmd":[]},' +
      '{"uuid":"B27218EA-7DEB-C42F-AC87-A7A4CB305671","name":"Steam Big Picture","cmd":"steam://open/bigpicture","wait-all":true,"auto-detach":true,"image-path":"steam.png"}' +
    '],"env":{},"version":2}',
    False);

  // --- Fix 5: DuoGamepadIsolator (servico Windows) ---
  Exec('sc.exe', 'stop {#ServiceName}',   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc.exe', 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{app}\DuoGamepadIsolator.exe'), '--install',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
    Exec(ExpandConstant('{app}\DuoGamepadIsolator.exe'), '--uninstall',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
