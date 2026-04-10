; Duo Manager Fix — Inno Setup Script
; Requer Inno Setup 6.x: https://jrsoftware.org/isinfo.php
;
; Para compilar:
;   1. Abra este arquivo no Inno Setup Compiler
;   2. Certifique-se de que os binarios estao em ..\bin\
;   3. Pressione F9 (Build)

#define AppName "Duo Manager Fix"
#define AppVersion "1.0.0"
#define AppPublisher "ThiagoRocha"
#define AppURL "https://github.com/ThiagoRocha/duo-manager-fix"
#define ServiceName "DuoGamepadIsolator"
#define DuoDir "C:\Program Files\Duo"
#define ApolloDir "C:\Program Files\Apollo"
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
SetupIconFile=
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardImageStretch=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
MinVersion=10.0
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\DuoGamepadIsolator.exe
CloseApplications=yes
; Nao precisa reiniciar (servico inicia sozinho)
AlwaysRestart=no

[Languages]
; Portugues como padrao, ingles como fallback
Name: "portuguese"; MessagesFile: "compiler:Languages\Portuguese.isl"
Name: "english";    MessagesFile: "compiler:Default.isl"

[Messages]
portuguese.WelcomeLabel1=Bem-vindo ao instalador do %1
portuguese.WelcomeLabel2=Este instalador vai corrigir tres problemas do Duo Manager 1.5.6:%n%n  1. Resolucao travada em 640x480%n  2. Interface web quebrada%n  3. Controles virtuais vazando entre sessoes%n%nClique em Avancar para continuar.
portuguese.FinishedLabel=O {#AppName} foi instalado com sucesso.%n%nO servico DuoGamepadIsolator esta em execucao. Conecte pelo Moonlight e teste.

[Files]
; Binarios pre-compilados (execute scripts\build.bat antes de compilar o instalador)
Source: "..\bin\DuoRdpWrapper.exe";      DestDir: "{tmp}";        Flags: deleteafterinstall
Source: "..\bin\DuoGamepadIsolator.exe"; DestDir: "{app}";        Flags: ignoreversion

[Code]
var
  DuoOk, ApolloOk: Boolean;

// Verifica pre-requisitos antes de iniciar
function InitializeSetup(): Boolean;
begin
  DuoOk    := FileExists(ExpandConstant('{#DuoDir}\Duo.exe'));
  ApolloOk := FileExists(ExpandConstant('{#ApolloDir}\sunshine.exe'));
  Result := True;

  if not DuoOk then begin
    MsgBox(
      'Duo Manager nao encontrado em:' + #13#10 +
      '  {#DuoDir}' + #13#10 + #13#10 +
      'Instale o Duo Manager 1.5.6 antes de continuar.' + #13#10 +
      'Download: https://github.com/YOUR_USERNAME/duo-manager-fix#prerequisites',
      mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;

  if not ApolloOk then begin
    MsgBox(
      'Apollo nao encontrado em:' + #13#10 +
      '  {#ApolloDir}' + #13#10 + #13#10 +
      'Instale o Apollo 0.4.6 antes de continuar.' + #13#10 +
      'Download: https://github.com/SudoMaker/Apollo/releases/tag/0.4.6',
      mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;
end;

// Instalacao pos-copia de arquivos
procedure CurStepChanged(CurStep: TSetupStep);
var
  DuoDir, ApolloDir: String;
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then Exit;

  DuoDir    := ExpandConstant('{#DuoDir}');
  ApolloDir := ExpandConstant('{#ApolloDir}');

  // --- Fix 1: DuoRdpWrapper (resolucao) ---
  // Tomar ownership e permissao do DuoRdp.exe
  Exec('takeown.exe', '/f "' + DuoDir + '\DuoRdp.exe" /a', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('icacls.exe',  '"' + DuoDir + '\DuoRdp.exe" /grant Administrators:F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Backup (apenas se ainda nao existir)
  if not FileExists(DuoDir + '\DuoRdp_orig.exe') then
    FileCopy(DuoDir + '\DuoRdp.exe', DuoDir + '\DuoRdp_orig.exe', False);
  // Substituir pelo wrapper
  FileCopy(ExpandConstant('{tmp}\DuoRdpWrapper.exe'), DuoDir + '\DuoRdp.exe', True);

  // --- Fix 2: Apollo sunshine.exe ---
  Exec('takeown.exe', '/f "' + DuoDir + '\sunshine.exe" /a', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('icacls.exe',  '"' + DuoDir + '\sunshine.exe" /grant Administrators:F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if not FileExists(DuoDir + '\sunshine_orig.exe') then
    FileCopy(DuoDir + '\sunshine.exe', DuoDir + '\sunshine_orig.exe', False);
  FileCopy(ApolloDir + '\sunshine.exe', DuoDir + '\sunshine.exe', True);

  // --- Fix 3: Web assets do Apollo ---
  ForceDirectories(DuoDir + '\assets\web\assets');
  FileCopy(ApolloDir + '\assets\web\pin.html',     DuoDir + '\assets\web\pin.html',     True);
  FileCopy(ApolloDir + '\assets\web\login.html',   DuoDir + '\assets\web\login.html',   True);
  FileCopy(ApolloDir + '\assets\web\welcome.html', DuoDir + '\assets\web\welcome.html', True);
  // Copiar todos os assets/ via xcopy
  Exec('xcopy.exe',
    '"' + ApolloDir + '\assets\web\assets\*" "' + DuoDir + '\assets\web\assets\" /Y /E /Q',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // --- Fix 4: Games_apps.json (sem prep-cmd problematicos) ---
  SaveStringToFile(
    DuoDir + '\config\Games_apps.json',
    '{"apps":[' +
      '{"uuid":"3C56B52A-50C8-3601-CC0D-042310A47F60","image-path":"desktop.png","name":"Desktop","prep-cmd":[]},' +
      '{"uuid":"B27218EA-7DEB-C42F-AC87-A7A4CB305671","name":"Steam Big Picture","cmd":"steam://open/bigpicture","wait-all":true,"auto-detach":true,"image-path":"steam.png"}' +
    '],"env":{},"version":2}',
    False);

  // --- Fix 5: DuoGamepadIsolator Windows service ---
  // Para e remove instancia anterior se existir
  Exec('sc.exe', 'stop {#ServiceName}',   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc.exe', 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Instalar e iniciar (o proprio exe faz isso com --install)
  Exec(ExpandConstant('{app}\DuoGamepadIsolator.exe'), '--install',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Desinstalacao: parar e remover servico
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then begin
    Exec(ExpandConstant('{app}\DuoGamepadIsolator.exe'), '--uninstall',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

[Run]
; Nada aqui — tudo e feito no CurStepChanged acima

[UninstallRun]
; Removido via CurUninstallStepChanged

[Icons]
; Sem atalhos de menu — e um fix de sistema, nao um app
