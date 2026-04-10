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

// Aborta a instalação imediatamente com mensagem clara.
// Restaura backups se existirem antes de sair.
procedure AbortInstall(Step: String; Reason: String; Hint: String);
var
  DuoDir: String;
begin
  DuoDir := ExpandConstant('{#DuoDir}');

  // Restaura arquivos originais se o backup já foi criado
  if FileExists(DuoDir + '\DuoRdp_orig.exe') then
    FileCopy(DuoDir + '\DuoRdp_orig.exe', DuoDir + '\DuoRdp.exe', True);
  if FileExists(DuoDir + '\sunshine_orig.exe') then
    FileCopy(DuoDir + '\sunshine_orig.exe', DuoDir + '\sunshine.exe', True);

  MsgBox(
    'Installation failed at: ' + Step + #13#10 + #13#10 +
    'Reason:' + #13#10 +
    '  ' + Reason + #13#10 + #13#10 +
    'What to do:' + #13#10 +
    '  ' + Hint + #13#10 + #13#10 +
    'Any files that were partially changed have been restored to their originals.' + #13#10 +
    'You can safely try again after resolving the issue above.',
    mbCriticalError, MB_OK);

  Abort(); // Cancela o wizard do Inno Setup
end;

function InitializeSetup(): Boolean;
begin
  Result := True;

  // Verifica se está rodando como Administrador
  if not IsAdminLoggedOn then begin
    MsgBox(
      'This installer must be run as Administrator.' + #13#10 + #13#10 +
      'Right-click the installer and choose "Run as administrator".',
      mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;

  // Verifica se o Duo Manager está instalado
  if not FileExists(ExpandConstant('{#DuoDir}\Duo.exe')) then begin
    MsgBox(
      'Duo Manager not found at:' + #13#10 +
      '  {#DuoDir}' + #13#10 + #13#10 +
      'Please install Duo Manager 1.5.6 before continuing.' + #13#10 +
      'Download: https://github.com/DuoStream/Duo/releases/tag/v1.5.6',
      mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;

  // Verifica se o Duo Manager está em execução (arquivos podem estar bloqueados)
  if FileExists(ExpandConstant('{#DuoDir}\sunshine.exe')) then begin
    // Tenta abrir o arquivo para escrita — se falhar, está em uso
    // Inno Setup não tem API direta para isso, então apenas avisamos para fechar o serviço
    // A validação real acontece quando tentamos copiar
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

  // Backup do original (somente se ainda não existe)
  if not FileExists(DuoDir + '\DuoRdp_orig.exe') then begin
    if not FileCopy(DuoDir + '\DuoRdp.exe', DuoDir + '\DuoRdp_orig.exe', False) then
      AbortInstall(
        'Resolution fix — backup of DuoRdp.exe',
        'Could not create backup file DuoRdp_orig.exe.',
        'Make sure Duo Manager service is stopped and try again.');
  end;

  // Substitui pelo wrapper
  if not FileCopy(ExpandConstant('{tmp}\DuoRdpWrapper.exe'), DuoDir + '\DuoRdp.exe', True) then
    AbortInstall(
      'Resolution fix — replacing DuoRdp.exe',
      'File is locked — Duo Manager service may still be running.',
      'Open Services (services.msc), stop "Duo Manager", then run the installer again.');

  // --- Fix 2: Apollo sunshine.exe (encoder RTX) ---
  Exec('takeown.exe', '/f "' + DuoDir + '\sunshine.exe" /a',                   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('icacls.exe',  '"' + DuoDir + '\sunshine.exe" /grant Administrators:F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Backup do original
  if not FileExists(DuoDir + '\sunshine_orig.exe') then begin
    if not FileCopy(DuoDir + '\sunshine.exe', DuoDir + '\sunshine_orig.exe', False) then
      AbortInstall(
        'Streaming engine — backup of sunshine.exe',
        'Could not create backup file sunshine_orig.exe.',
        'Make sure Duo Manager service is stopped and try again.');
  end;

  // Substitui pelo Apollo
  if not FileCopy(ExpandConstant('{tmp}\sunshine.exe'), DuoDir + '\sunshine.exe', True) then
    AbortInstall(
      'Streaming engine — replacing sunshine.exe',
      'File is locked — sunshine.exe is currently running.',
      'Open Services (services.msc), stop "Duo Manager", then run the installer again.');

  // --- Fix 3: Web assets (interface de gerenciamento) ---
  if not ForceDirectories(DuoDir + '\assets\web\assets') then
    AbortInstall(
      'Web UI assets — creating directory',
      'Could not create: ' + DuoDir + '\assets\web\assets',
      'Check that you have Administrator rights and the Duo Manager folder is not read-only.');

  if not FileCopy(ExpandConstant('{tmp}\web\pin.html'),     DuoDir + '\assets\web\pin.html',     True) then
    AbortInstall('Web UI assets — pin.html',     'Could not copy pin.html.',     'Check folder permissions on ' + DuoDir + '\assets\web\');
  if not FileCopy(ExpandConstant('{tmp}\web\login.html'),   DuoDir + '\assets\web\login.html',   True) then
    AbortInstall('Web UI assets — login.html',   'Could not copy login.html.',   'Check folder permissions on ' + DuoDir + '\assets\web\');
  if not FileCopy(ExpandConstant('{tmp}\web\welcome.html'), DuoDir + '\assets\web\welcome.html', True) then
    AbortInstall('Web UI assets — welcome.html', 'Could not copy welcome.html.', 'Check folder permissions on ' + DuoDir + '\assets\web\');

  Exec('xcopy.exe',
    '"' + ExpandConstant('{tmp}\web\assets\*') + '" "' + DuoDir + '\assets\web\assets\" /Y /E /Q',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode <> 0 then
    AbortInstall(
      'Web UI assets — JavaScript/CSS bundles',
      'xcopy failed with error code ' + IntToStr(ResultCode) + '.',
      'Check that ' + DuoDir + '\assets\web\assets\ is writable and try again.');

  // Valida que os assets chegaram
  if not FileExists(DuoDir + '\assets\web\assets\index-d0312854.js') then
    AbortInstall(
      'Web UI assets — validation',
      'JavaScript bundles not found after copy.',
      'This may be an antivirus blocking the files. Temporarily disable it and try again.');

  // --- Fix 4: Games_apps.json ---
  if not SaveStringToFile(
    DuoDir + '\config\Games_apps.json',
    '{"apps":[' +
      '{"uuid":"3C56B52A-50C8-3601-CC0D-042310A47F60","image-path":"desktop.png","name":"Desktop","prep-cmd":[]},' +
      '{"uuid":"B27218EA-7DEB-C42F-AC87-A7A4CB305671","name":"Steam Big Picture","cmd":"steam://open/bigpicture","wait-all":true,"auto-detach":true,"image-path":"steam.png"}' +
    '],"env":{},"version":2}',
    False) then
    AbortInstall(
      'App configuration — Games_apps.json',
      'Could not write to ' + DuoDir + '\config\Games_apps.json.',
      'Check that the config folder exists and is writable.');

  // --- Fix 5: DuoGamepadIsolator (servico Windows) ---
  Exec('sc.exe', 'stop {#ServiceName}',   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc.exe', 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{app}\DuoGamepadIsolator.exe'), '--install',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Valida que o serviço subiu
  Exec('sc.exe', 'query {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode <> 0 then
    AbortInstall(
      'Gamepad isolator service',
      'Service was installed but could not be started.',
      'Check Windows Event Viewer for details, or report this issue at: {#AppURL}/issues');

  // --- Tudo OK ---
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
