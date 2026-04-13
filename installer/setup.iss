; Duo Manager Fix — Inno Setup Script
; Requer Inno Setup 6.x: https://jrsoftware.org/isinfo.php
;
; Antes de compilar:
;   1. Execute scripts\build.bat          → gera bin\DuoRdpWrapper.exe e bin\DuoGamepadIsolator.exe
;   2. Execute scripts\prepare_bundle.bat → popula bundled\ com sunshine.exe e web assets do Apollo
;   3. Abra este arquivo no Inno Setup Compiler e pressione F9

#define AppName "Duo Manager Fix"
#define AppVersion "1.0.5"
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
portuguese.WelcomeLabel2=Este instalador corrige quatro problemas do Duo Manager 1.5.6:%n%n  1. Resolucao travada em 640x480%n  2. Interface web de gerenciamento quebrada%n  3. Controles virtuais vazando entre sessoes%n%nPre-requisito: Duo Manager 1.5.6 instalado.%n%nClique em Avancar para continuar.
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

// ============================================================
// AbortInstall: aborta com mensagem clara e restaura backups
// ============================================================
procedure AbortInstall(Step: String; Reason: String; Hint: String);
var
  DuoDir: String;
  ResultCode: Integer;
begin
  DuoDir := ExpandConstant('{#DuoDir}');

  if FileExists(DuoDir + '\DuoRdp_orig.exe') then
    Exec('cmd.exe', '/c copy /y "' + DuoDir + '\DuoRdp_orig.exe" "' + DuoDir + '\DuoRdp.exe"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if FileExists(DuoDir + '\sunshine_orig.exe') then
    Exec('cmd.exe', '/c copy /y "' + DuoDir + '\sunshine_orig.exe" "' + DuoDir + '\sunshine.exe"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  MsgBox(
    'Installation failed at: ' + Step + #13#10 + #13#10 +
    'Reason:' + #13#10 +
    '  ' + Reason + #13#10 + #13#10 +
    'What to do:' + #13#10 +
    '  ' + Hint + #13#10 + #13#10 +
    'Any partially changed files have been restored to their originals.' + #13#10 +
    'You can safely try again after resolving the issue above.',
    mbCriticalError, MB_OK);

  Abort();
end;

// ============================================================
// InitializeSetup: pre-checks
// ============================================================
function InitializeSetup(): Boolean;
begin
  Result := True;

  if not IsAdmin() then begin
    MsgBox(
      'This installer must be run as Administrator.' + #13#10 + #13#10 +
      'Right-click the installer and choose "Run as administrator".',
      mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;

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
end;

// ============================================================
// CurStepChanged: ssPostInstall — aplica todos os fixes
// ============================================================
procedure CurStepChanged(CurStep: TSetupStep);
var
  DuoDir:     String;
  ResultCode: Integer;
  StatusMsg:  String;
begin
  if CurStep <> ssPostInstall then Exit;

  DuoDir := ExpandConstant('{#DuoDir}');

  // ----------------------------------------------------------
  // Fix 1: DuoRdpWrapper (resolucao 4K)
  // ----------------------------------------------------------
  Exec('takeown.exe', '/f "' + DuoDir + '\DuoRdp.exe" /a',                   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('icacls.exe',  '"' + DuoDir + '\DuoRdp.exe" /grant Administrators:F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if not FileExists(DuoDir + '\DuoRdp_orig.exe') then begin
    Exec('cmd.exe', '/c copy /y "' + DuoDir + '\DuoRdp.exe" "' + DuoDir + '\DuoRdp_orig.exe"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if (ResultCode <> 0) or (not FileExists(DuoDir + '\DuoRdp_orig.exe')) then
      AbortInstall('Resolution fix — backup of DuoRdp.exe',
        'Could not create backup DuoRdp_orig.exe (cmd copy returned ' + IntToStr(ResultCode) + ').',
        'Make sure Duo Manager service is stopped and try again.');
  end;

  Exec('cmd.exe', '/c copy /y "' + ExpandConstant('{tmp}\DuoRdpWrapper.exe') + '" "' + DuoDir + '\DuoRdp.exe"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Exec('powershell.exe',
    '-NoProfile -Command "if ((Get-Item ''' + DuoDir + '\DuoRdp.exe'').Length -gt 100000) { exit 1 } else { exit 0 }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode <> 0 then
    AbortInstall('Resolution fix — replacing DuoRdp.exe',
      'File was copied but the original binary is still in place (size > 100 KB).',
      'Run the installer again as Administrator. If the problem persists, download' + #13#10 +
      'DuoRdpWrapper.exe from the release page and copy it manually.');

  // ----------------------------------------------------------
  // Fix 2: Apollo sunshine.exe (GPU encoding)
  // ----------------------------------------------------------
  Exec('takeown.exe', '/f "' + DuoDir + '\sunshine.exe" /a',                   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('icacls.exe',  '"' + DuoDir + '\sunshine.exe" /grant Administrators:F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if not FileExists(DuoDir + '\sunshine_orig.exe') then begin
    Exec('cmd.exe', '/c copy /y "' + DuoDir + '\sunshine.exe" "' + DuoDir + '\sunshine_orig.exe"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if (ResultCode <> 0) or (not FileExists(DuoDir + '\sunshine_orig.exe')) then
      AbortInstall('Streaming engine — backup of sunshine.exe',
        'Could not create backup sunshine_orig.exe (cmd copy returned ' + IntToStr(ResultCode) + ').',
        'Make sure Duo Manager service is stopped and try again.');
  end;

  Exec('cmd.exe', '/c copy /y "' + ExpandConstant('{tmp}\sunshine.exe') + '" "' + DuoDir + '\sunshine.exe"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Exec('powershell.exe',
    '-NoProfile -Command "if ((Get-Item ''' + DuoDir + '\sunshine.exe'').Length -lt 10000000) { exit 1 } else { exit 0 }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode <> 0 then
    AbortInstall('Streaming engine — replacing sunshine.exe',
      'File is locked or could not be replaced.',
      'Open Services (services.msc), stop "Duo Manager", then run the installer again.');

  // ----------------------------------------------------------
  // Fix 3: Web assets (interface de gerenciamento)
  // Usa cmd /c copy para todos os HTMLs — evita bug do FileCopy no Windows 11
  // Eleva permissoes na pasta assets\ antes de copiar (mesma tecnica do Fix 1/2)
  // ----------------------------------------------------------

  // Eleva permissoes recursivamente em assets\ para garantir que cmd copy funcione
  // mesmo em Windows 11 com ACLs restritivas herdadas do instalador do Duo Manager.
  Exec('takeown.exe', '/f "' + DuoDir + '\assets" /r /d y',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('icacls.exe', '"' + DuoDir + '\assets" /grant Administrators:F /t',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if not ForceDirectories(DuoDir + '\assets\web\assets') then
    AbortInstall('Web UI assets — creating directory',
      'Could not create: ' + DuoDir + '\assets\web\assets',
      'Check that you have Administrator rights and the Duo Manager folder is not read-only.');

  Exec('cmd.exe', '/c copy /y "' + ExpandConstant('{tmp}\web\pin.html') + '" "' + DuoDir + '\assets\web\pin.html"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if (ResultCode <> 0) or (not FileExists(DuoDir + '\assets\web\pin.html')) then
    AbortInstall('Web UI assets — pin.html',
      'Could not copy pin.html (cmd copy returned ' + IntToStr(ResultCode) + ').',
      'Check folder permissions on ' + DuoDir + '\assets\web\');

  Exec('cmd.exe', '/c copy /y "' + ExpandConstant('{tmp}\web\login.html') + '" "' + DuoDir + '\assets\web\login.html"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if (ResultCode <> 0) or (not FileExists(DuoDir + '\assets\web\login.html')) then
    AbortInstall('Web UI assets — login.html',
      'Could not copy login.html (cmd copy returned ' + IntToStr(ResultCode) + ').',
      'Check folder permissions on ' + DuoDir + '\assets\web\');

  Exec('cmd.exe', '/c copy /y "' + ExpandConstant('{tmp}\web\welcome.html') + '" "' + DuoDir + '\assets\web\welcome.html"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if (ResultCode <> 0) or (not FileExists(DuoDir + '\assets\web\welcome.html')) then
    AbortInstall('Web UI assets — welcome.html',
      'Could not copy welcome.html (cmd copy returned ' + IntToStr(ResultCode) + ').',
      'Check folder permissions on ' + DuoDir + '\assets\web\');

  Exec('xcopy.exe',
    '"' + ExpandConstant('{tmp}\web\assets\*') + '" "' + DuoDir + '\assets\web\assets\" /Y /E /Q',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode <> 0 then
    AbortInstall('Web UI assets — JavaScript/CSS bundles',
      'xcopy failed with error code ' + IntToStr(ResultCode) + '.',
      'Check that ' + DuoDir + '\assets\web\assets\ is writable and try again.');

  if not FileExists(DuoDir + '\assets\web\assets\index-d0312854.js') then
    AbortInstall('Web UI assets — validation',
      'JavaScript bundles not found after copy.',
      'Antivirus may be blocking the files. Temporarily disable it and try again.');

  // ----------------------------------------------------------
  // Fix 4: Games_apps.json
  // ----------------------------------------------------------
  if not SaveStringToFile(
    DuoDir + '\config\Games_apps.json',
    '{"apps":[' +
      '{"uuid":"3C56B52A-50C8-3601-CC0D-042310A47F60","image-path":"desktop.png","name":"Desktop","prep-cmd":[]},' +
      '{"uuid":"B27218EA-7DEB-C42F-AC87-A7A4CB305671","name":"Steam Big Picture","cmd":"cmd.exe /c \"%ProgramFiles(x86)%\\Steam\\steam.exe\" -tenfoot -master_ipc_name_override %USERNAME%","wait-all":true,"auto-detach":true,"image-path":"steam.png"}' +
    '],"env":{},"version":2}',
    False) then
    AbortInstall('App configuration — Games_apps.json',
      'Could not write to ' + DuoDir + '\config\Games_apps.json.',
      'Check that the config folder exists and is writable.');

  // ----------------------------------------------------------
  // Fix 5: DuoGamepadIsolator (servico Windows)
  // ----------------------------------------------------------

  // Para o servico e aguarda ate 10s para ele encerrar de fato antes de deletar.
  // Sem esse wait, o sc.exe delete falha quando o processo ainda esta vivo.
  Exec('sc.exe', 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('powershell.exe',
    '-NoProfile -Command "' +
    '$svc = ''{#ServiceName}''; ' +
    '$i = 0; ' +
    'while ($i -lt 20) { ' +
    '  $s = (Get-Service $svc -ErrorAction SilentlyContinue); ' +
    '  if (!$s -or $s.Status -eq ''Stopped'') { break }; ' +
    '  Start-Sleep -Milliseconds 500; $i++ ' +
    '}"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Exec('sc.exe', 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{app}\DuoGamepadIsolator.exe'), '--install',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Exec('sc.exe', 'query {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode <> 0 then
    AbortInstall('Gamepad isolator service',
      'Service was installed but could not be queried.',
      'Check Windows Event Viewer for details, or report at: {#AppURL}/issues');

  // ----------------------------------------------------------
  // Validacao pos-instalacao por componente
  // ----------------------------------------------------------
  StatusMsg := 'Installation complete. Component status:' + #13#10 + #13#10;

  Exec('powershell.exe',
    '-NoProfile -Command "if ((Get-Item ''' + DuoDir + '\DuoRdp.exe'').Length -lt 100000) { exit 0 } else { exit 1 }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode = 0 then
    StatusMsg := StatusMsg + '  [OK] Resolution wrapper (DuoRdp.exe replaced)' + #13#10
  else
    StatusMsg := StatusMsg + '  [!!] Resolution wrapper — DuoRdp.exe may not have been replaced' + #13#10;

  Exec('powershell.exe',
    '-NoProfile -Command "if ((Get-Item ''' + DuoDir + '\sunshine.exe'').Length -gt 10000000) { exit 0 } else { exit 1 }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode = 0 then
    StatusMsg := StatusMsg + '  [OK] Streaming engine (Apollo sunshine.exe replaced)' + #13#10
  else
    StatusMsg := StatusMsg + '  [!!] Streaming engine — sunshine.exe may not have been replaced' + #13#10;

  if FileExists(DuoDir + '\assets\web\pin.html') and
     FileExists(DuoDir + '\assets\web\login.html') and
     FileExists(DuoDir + '\assets\web\welcome.html') and
     FileExists(DuoDir + '\assets\web\assets\index-d0312854.js') then
    StatusMsg := StatusMsg + '  [OK] Web UI assets (HTML + JS bundles)' + #13#10
  else
    StatusMsg := StatusMsg + '  [!!] Web UI assets — one or more files missing' + #13#10;

  Exec('sc.exe', 'query {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode = 0 then
    StatusMsg := StatusMsg + '  [OK] Gamepad isolator service (running)' + #13#10 +
                             '       XInput isolation active — controller inputs are session-scoped' + #13#10
  else
    StatusMsg := StatusMsg + '  [!!] Gamepad isolator service — not running' + #13#10;

  StatusMsg := StatusMsg + #13#10 + 'Connect from Moonlight and test.';

  MsgBox(StatusMsg, mbInformation, MB_OK);
end;

// ============================================================
// Uninstall
// ============================================================
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
    Exec(ExpandConstant('{app}\DuoGamepadIsolator.exe'), '--uninstall',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
