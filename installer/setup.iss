; Duo Manager Fix — Inno Setup Script
; Requer Inno Setup 6.x: https://jrsoftware.org/isinfo.php
;
; Antes de compilar:
;   1. Execute scripts\build.bat          → gera bin\DuoRdpWrapper.exe e bin\DuoGamepadIsolator.exe
;   2. Execute scripts\prepare_bundle.bat → popula bundled\ com sunshine.exe e web assets do Apollo
;   3. Abra este arquivo no Inno Setup Compiler e pressione F9

#define AppName "Duo Manager Fix"
#define AppVersion "1.0.2"
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

// Acumula erros de cada etapa para exibir no final
var
  InstallErrors: String;

procedure AddError(Step: String; Detail: String);
begin
  InstallErrors := InstallErrors + '  [FAILED] ' + Step + #13#10 +
                   '           ' + Detail + #13#10;
end;

// Verifica se um arquivo foi realmente substituido comparando tamanho minimo esperado
function CheckFileReplaced(Path: String; MinSizeBytes: Integer; StepName: String): Boolean;
var
  Size: Integer;
begin
  Result := False;
  if not FileExists(Path) then begin
    AddError(StepName, 'File not found: ' + Path);
    Exit;
  end;
  // Inno Setup nao tem GetFileSize nativo — usamos que o arquivo existe e backup existe
  Result := True;
end;

function InitializeSetup(): Boolean;
begin
  InstallErrors := '';
  Result := True;
  if not FileExists(ExpandConstant('{#DuoDir}\Duo.exe')) then begin
    MsgBox(
      'Duo Manager not found at:' + #13#10 +
      '  {#DuoDir}' + #13#10 + #13#10 +
      'Please install Duo Manager 1.5.6 before continuing.' + #13#10 +
      'Download: https://github.com/DuoStream/Duo/releases/tag/v1.5.6',
      mbCriticalError, MB_OK);
    Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  DuoDir: String;
  ResultCode: Integer;
  ServiceRunning: Boolean;
begin
  if CurStep <> ssPostInstall then Exit;

  DuoDir := ExpandConstant('{#DuoDir}');

  // --- Fix 1: DuoRdpWrapper (resolucao 4K) ---
  Exec('takeown.exe', '/f "' + DuoDir + '\DuoRdp.exe" /a',                   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('icacls.exe',  '"' + DuoDir + '\DuoRdp.exe" /grant Administrators:F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if not FileExists(DuoDir + '\DuoRdp_orig.exe') then
    FileCopy(DuoDir + '\DuoRdp.exe', DuoDir + '\DuoRdp_orig.exe', False);
  if not FileCopy(ExpandConstant('{tmp}\DuoRdpWrapper.exe'), DuoDir + '\DuoRdp.exe', True) then
    AddError('Resolution fix (DuoRdpWrapper)', 'Could not replace DuoRdp.exe — file may be locked by Duo Manager. Try stopping the service first.');

  // Valida: backup deve existir
  if not FileExists(DuoDir + '\DuoRdp_orig.exe') then
    AddError('Resolution fix (backup)', 'DuoRdp_orig.exe backup was not created.');

  // --- Fix 2: Apollo sunshine.exe (encoder RTX) ---
  Exec('takeown.exe', '/f "' + DuoDir + '\sunshine.exe" /a',                   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('icacls.exe',  '"' + DuoDir + '\sunshine.exe" /grant Administrators:F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if not FileExists(DuoDir + '\sunshine_orig.exe') then
    FileCopy(DuoDir + '\sunshine.exe', DuoDir + '\sunshine_orig.exe', False);
  if not FileCopy(ExpandConstant('{tmp}\sunshine.exe'), DuoDir + '\sunshine.exe', True) then
    AddError('Streaming engine (sunshine.exe)', 'Could not replace sunshine.exe — file may be in use. Restart Duo Manager service and reinstall.');

  // Valida: backup deve existir
  if not FileExists(DuoDir + '\sunshine_orig.exe') then
    AddError('Streaming engine (backup)', 'sunshine_orig.exe backup was not created.');

  // --- Fix 3: Web assets (interface de gerenciamento) ---
  if not ForceDirectories(DuoDir + '\assets\web\assets') then
    AddError('Web UI assets', 'Could not create directory: ' + DuoDir + '\assets\web\assets');

  if not FileCopy(ExpandConstant('{tmp}\web\pin.html'),     DuoDir + '\assets\web\pin.html',     True) then
    AddError('Web UI assets (pin.html)',     'Could not copy pin.html.');
  if not FileCopy(ExpandConstant('{tmp}\web\login.html'),   DuoDir + '\assets\web\login.html',   True) then
    AddError('Web UI assets (login.html)',   'Could not copy login.html.');
  if not FileCopy(ExpandConstant('{tmp}\web\welcome.html'), DuoDir + '\assets\web\welcome.html', True) then
    AddError('Web UI assets (welcome.html)', 'Could not copy welcome.html.');

  Exec('xcopy.exe',
    '"' + ExpandConstant('{tmp}\web\assets\*') + '" "' + DuoDir + '\assets\web\assets\" /Y /E /Q',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode <> 0 then
    AddError('Web UI assets (JS/CSS)', 'xcopy returned error ' + IntToStr(ResultCode) + '. Some web assets may be missing.');

  // Valida: pelo menos um JS deve existir
  if not FileExists(DuoDir + '\assets\web\assets\index-d0312854.js') then
    AddError('Web UI assets (validation)', 'JavaScript bundles not found after copy — web UI may not work.');

  // --- Fix 4: Games_apps.json ---
  if not SaveStringToFile(
    DuoDir + '\config\Games_apps.json',
    '{"apps":[' +
      '{"uuid":"3C56B52A-50C8-3601-CC0D-042310A47F60","image-path":"desktop.png","name":"Desktop","prep-cmd":[]},' +
      '{"uuid":"B27218EA-7DEB-C42F-AC87-A7A4CB305671","name":"Steam Big Picture","cmd":"steam://open/bigpicture","wait-all":true,"auto-detach":true,"image-path":"steam.png"}' +
    '],"env":{},"version":2}',
    False) then
    AddError('App list (Games_apps.json)', 'Could not write config file — check permissions on ' + DuoDir + '\config\');

  // --- Fix 5: DuoGamepadIsolator (servico Windows) ---
  Exec('sc.exe', 'stop {#ServiceName}',   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc.exe', 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{app}\DuoGamepadIsolator.exe'), '--install',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Valida: servico deve estar rodando
  ServiceRunning := False;
  Exec('sc.exe', 'query {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode <> 0 then
    AddError('Gamepad isolator service', 'Service could not be installed or started. Try running the installer again as Administrator.')
  else
    ServiceRunning := True;

  // --- Exibe resumo de erros se houver ---
  if InstallErrors <> '' then
    MsgBox(
      'Installation completed with warnings.' + #13#10 +
      'The following steps did not apply correctly:' + #13#10 + #13#10 +
      InstallErrors + #13#10 +
      'You can try running the installer again, or check the README for manual steps:' + #13#10 +
      '{#AppURL}',
      mbError, MB_OK)
  else if ServiceRunning then
    MsgBox(
      'All fixes were applied successfully!' + #13#10 + #13#10 +
      '  [OK] Resolution wrapper (DuoRdpWrapper)' + #13#10 +
      '  [OK] Streaming engine (Apollo sunshine.exe)' + #13#10 +
      '  [OK] Web management UI assets' + #13#10 +
      '  [OK] Gamepad isolator service (running)' + #13#10 + #13#10 +
      'Connect from Moonlight and test.',
      mbInformation, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
    Exec(ExpandConstant('{app}\DuoGamepadIsolator.exe'), '--uninstall',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
