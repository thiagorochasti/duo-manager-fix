; Duo Manager Fix — Inno Setup Script (Dual Engine: Apollo + Sunshine)
; Requer Inno Setup 6.x: https://jrsoftware.org/isinfo.php
;
; Antes de compilar:
;   1. Execute scripts\build.bat          → gera bin\DuoRdpWrapper.exe e bin\DuoGamepadIsolator.exe
;   2. Execute scripts\prepare_bundle.bat → popula bundled\apollo\ e bundled\sunshine\
;   3. Abra este arquivo no Inno Setup Compiler e pressione F9

#define AppName "Duo Manager Fix"
#define AppVersion "1.0.7"
#define AppPublisher "thiagorochasti"
#define AppURL "https://github.com/thiagorochasti/duo-manager-fix"
#define ServiceName "DuoGamepadIsolator"
#define DuoDir "C:\Program Files\Duo"


[Setup]
AppId={{8F2A1C3E-4B7D-4E9F-A1C2-3D5E6F7A8B9C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
DefaultDirName={autopf}\{#AppName}

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
UninstallDisplayIcon={#DuoDir}\Duo.exe

CloseApplications=yes
AlwaysRestart=no

[Languages]
Name: "portuguese"; MessagesFile: "compiler:Languages\Portuguese.isl"
Name: "english";    MessagesFile: "compiler:Default.isl"

[Messages]
portuguese.WelcomeLabel1=Bem-vindo ao instalador do %1
portuguese.WelcomeLabel2=Este instalador corrige problemas do Duo Manager 1.5.6:%n%n  1. Resolucao travada em 640x480%n  2. Interface web de gerenciamento quebrada%n%nVoce podera escolher entre Apollo (estavel) ou Sunshine nativo (experimental).%n%nPre-requisito: Duo Manager 1.5.6 instalado.%n%nClique em Avancar para continuar.
portuguese.FinishedLabel=Instalacao concluida!%n%nConecte pelo Moonlight e teste.


[Types]
Name: "apollo";   Description: "Apollo 0.4.6 (estavel — padrao)"
Name: "sunshine"; Description: "Sunshine Nativo (experimental — melhor suporte HID/DualSense)"

[Components]
Name: "engine_apollo";   Description: "Apollo 0.4.6 — engine de streaming testada e estavel";     Types: apollo;   Flags: exclusive
Name: "engine_sunshine"; Description: "Sunshine Nativo — suporte melhorado a HID, DualSense e multi-sessao"; Types: sunshine; Flags: exclusive

[Files]
; Binarios compilados (sempre instalados)
Source: "..\bin\DuoRdpWrapper.exe";      DestDir: "{tmp}"; Flags: deleteafterinstall


; === Apollo engine ===
Source: "..\bundled\apollo\sunshine.exe";       DestDir: "{tmp}\engine"; Components: engine_apollo; Flags: deleteafterinstall
Source: "..\bundled\apollo\web\pin.html";       DestDir: "{tmp}\web";    Components: engine_apollo; Flags: deleteafterinstall
Source: "..\bundled\apollo\web\login.html";     DestDir: "{tmp}\web";    Components: engine_apollo; Flags: deleteafterinstall
Source: "..\bundled\apollo\web\welcome.html";   DestDir: "{tmp}\web";    Components: engine_apollo; Flags: deleteafterinstall
Source: "..\bundled\apollo\web\assets\*";       DestDir: "{tmp}\web\assets"; Components: engine_apollo; Flags: recursesubdirs deleteafterinstall

; === Sunshine engine ===
Source: "..\bundled\sunshine\sunshine.exe";      DestDir: "{tmp}\engine"; Components: engine_sunshine; Flags: deleteafterinstall
Source: "..\bundled\sunshine\zlib1.dll";         DestDir: "{tmp}\engine"; Components: engine_sunshine; Flags: deleteafterinstall
Source: "..\bundled\sunshine\assets\web\*";      DestDir: "{tmp}\web";    Components: engine_sunshine; Flags: recursesubdirs deleteafterinstall
Source: "..\bundled\sunshine\assets\*";          DestDir: "{tmp}\sunshine_assets"; Components: engine_sunshine; Flags: recursesubdirs deleteafterinstall
Source: "..\bundled\sunshine\scripts\*";         DestDir: "{tmp}\sunshine_scripts"; Components: engine_sunshine; Flags: recursesubdirs deleteafterinstall
Source: "..\bundled\sunshine\tools\*";           DestDir: "{tmp}\sunshine_tools";   Components: engine_sunshine; Flags: recursesubdirs deleteafterinstall

[Code]

// ============================================================
// Helper: detecta qual engine foi selecionada
// ============================================================
function IsSunshine: Boolean;
begin
  Result := WizardIsComponentSelected('engine_sunshine');
end;

function IsApollo: Boolean;
begin
  Result := WizardIsComponentSelected('engine_apollo');
end;

function EngineName: String;
begin
  if IsSunshine then Result := 'Sunshine'
  else Result := 'Apollo';
end;

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
    'Engine: ' + EngineName + #13#10 + #13#10 +
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
  if CurStep = ssInstall then
  begin
    // Limpeza de legado (Pre-Install): Garante que o servico isolador antigo seja removido do sistema
    Exec('sc.exe', 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('sc.exe', 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

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
  // Fix 2: Streaming engine (Apollo OU Sunshine)
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

  // Copia sunshine.exe da engine selecionada
  Exec('cmd.exe', '/c copy /y "' + ExpandConstant('{tmp}\engine\sunshine.exe') + '" "' + DuoDir + '\sunshine.exe"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Exec('powershell.exe',
    '-NoProfile -Command "if ((Get-Item ''' + DuoDir + '\sunshine.exe'').Length -lt 10000000) { exit 1 } else { exit 0 }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode <> 0 then
    AbortInstall('Streaming engine — replacing sunshine.exe',
      'File is locked or could not be replaced.',
      'Open Services (services.msc), stop "Duo Manager", then run the installer again.');

  // Sunshine: copia zlib1.dll e assets extras
  if IsSunshine then begin
    Exec('cmd.exe', '/c copy /y "' + ExpandConstant('{tmp}\engine\zlib1.dll') + '" "' + DuoDir + '\zlib1.dll"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Assets completos do Sunshine (configs, web UI estendida)
    Exec('takeown.exe', '/f "' + DuoDir + '\assets" /r /d y',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('icacls.exe', '"' + DuoDir + '\assets" /grant Administrators:F /t',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    Exec('xcopy.exe',
      '"' + ExpandConstant('{tmp}\sunshine_assets\*') + '" "' + DuoDir + '\assets\" /Y /E /Q',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Scripts e tools do Sunshine
    Exec('xcopy.exe',
      '"' + ExpandConstant('{tmp}\sunshine_scripts\*') + '" "' + DuoDir + '\scripts\" /Y /E /Q',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('xcopy.exe',
      '"' + ExpandConstant('{tmp}\sunshine_tools\*') + '" "' + DuoDir + '\tools\" /Y /E /Q',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  // ----------------------------------------------------------
  // Fix 3: Web assets (interface de gerenciamento)
  // ----------------------------------------------------------
  Exec('takeown.exe', '/f "' + DuoDir + '\assets" /r /d y',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('icacls.exe', '"' + DuoDir + '\assets" /grant Administrators:F /t',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if not ForceDirectories(DuoDir + '\assets\web\assets') then
    AbortInstall('Web UI assets — creating directory',
      'Could not create: ' + DuoDir + '\assets\web\assets',
      'Check that you have Administrator rights and the Duo Manager folder is not read-only.');

  // Copia web assets da engine selecionada
  Exec('xcopy.exe',
    '"' + ExpandConstant('{tmp}\web\*') + '" "' + DuoDir + '\assets\web\" /Y /E /Q',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode <> 0 then
    AbortInstall('Web UI assets — copy',
      'xcopy failed with error code ' + IntToStr(ResultCode) + '.',
      'Check that ' + DuoDir + '\assets\web\ is writable and try again.');

  // Validacao: pin.html existe em ambas as engines
  if not FileExists(DuoDir + '\assets\web\pin.html') then
    AbortInstall('Web UI assets — validation',
      'pin.html not found after copy.',
      'Antivirus may be blocking the files. Temporarily disable it and try again.');

  // ----------------------------------------------------------
  // Fix 4: Games_apps.json (somente se nao existir — preserva customizacoes do usuario)
  // ----------------------------------------------------------
  if not ForceDirectories(DuoDir + '\config') then
    AbortInstall('App configuration — creating config directory',
      'Could not create: ' + DuoDir + '\config',
      'Check that you have Administrator rights and the Duo Manager folder is not read-only.');

  if not FileExists(DuoDir + '\config\Games_apps.json') then begin
    if not SaveStringToFile(
      DuoDir + '\config\Games_apps.json',
      '{"apps":[' +
        '{"uuid":"3C56B52A-50C8-3601-CC0D-042310A47F60","image-path":"desktop.png","name":"Desktop","prep-cmd":[]},' +
        '{"uuid":"B27218EA-7DEB-C42F-AC87-A7A4CB305671","name":"Steam Big Picture","cmd":"steam://open/bigpicture","prep-cmd":[{"do":"","undo":"steam://close/bigpicture"}],"wait-all":true,"auto-detach":true,"image-path":"steam.png"}' +
      '],"env":{},"version":2}',
      False) then
      AbortInstall('App configuration — Games_apps.json',
        'Could not write to ' + DuoDir + '\config\Games_apps.json.',
        'Check that the config folder exists and is writable.');
  end;


  // ----------------------------------------------------------
  // Fix 5: Games.conf — habilitar logging para resolucao automatica
  // O wrapper le "Desktop resolution [WxH]" do Games.log para saber a
  // resolucao que o Moonlight negociou. Requer min_log_level = info.
  // ----------------------------------------------------------
  if FileExists(DuoDir + '\config\Games.conf') then begin
    Exec('powershell.exe',
      '-NoProfile -Command "' +
      '$f = ''' + DuoDir + '\config\Games.conf''; ' +
      '$c = Get-Content $f -Raw; ' +
      'if ($c -match ''min_log_level\s*=\s*none'') { ' +
      '  $c = $c -replace ''min_log_level\s*=\s*none'', ''min_log_level = info''; ' +
      '  $c | Set-Content $f -NoNewline; ' +
      '}"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  // ----------------------------------------------------------
  // Validacao pos-instalacao por componente
  // ----------------------------------------------------------
  StatusMsg := 'Installation complete (' + EngineName + ' engine).' + #13#10 +
               'Component status:' + #13#10 + #13#10;

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
    StatusMsg := StatusMsg + '  [OK] Streaming engine (' + EngineName + ' sunshine.exe replaced)' + #13#10
  else
    StatusMsg := StatusMsg + '  [!!] Streaming engine — sunshine.exe may not have been replaced' + #13#10;

  if FileExists(DuoDir + '\assets\web\pin.html') then
    StatusMsg := StatusMsg + '  [OK] Web UI assets' + #13#10
  else
    StatusMsg := StatusMsg + '  [!!] Web UI assets — one or more files missing' + #13#10;

  if IsSunshine then
    StatusMsg := StatusMsg + #13#10 + 'NOTE: You selected Sunshine native engine.' + #13#10 +
                             'HidHide support is improved. Test with ds4windows if needed.';

  StatusMsg := StatusMsg + #13#10 + #13#10 + 'Connect from Moonlight and test.';

  MsgBox(StatusMsg, mbInformation, MB_OK);

end;


// ============================================================
// Uninstall
// ============================================================
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Limpeza de legado: Garante que o Gamepad Isolator antigo (se existir) seja removido
    Exec('sc.exe', 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('sc.exe', 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

end.

