; Duo Manager Fix - Inno Setup Script (Sunshine Engine)
; Requires Inno Setup 6.x: https://jrsoftware.org/isinfo.php
;
; Before compiling:
;   1. Run scripts\build.bat          -> generates bin\DuoRdpWrapper.exe
;   2. Run scripts\prepare_bundle.bat -> populates bundled\sunshine\
;   3. Open this file in Inno Setup Compiler and press F9

#define AppName "Duo Manager Fix"
#define AppVersion "1.0.8"
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
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel1=Welcome to the %1 Setup Wizard
WelcomeLabel2=This installer fixes known issues in Duo Manager 1.5.6:%n%n  1. Resolution locked at 640x480%n  2. Broken web management interface%n%nUses native Sunshine engine (better HID, DualSense and multi-session support).%n%nPrerequisite: Duo Manager 1.5.6 must be installed.%n%nClick Next to continue.
FinishedLabel=Installation complete!%n%nConnect from Moonlight and test.

[Files]
; Compiled binaries (always installed)
Source: "..\bin\DuoRdpWrapper.exe";  DestDir: "{tmp}"; Flags: deleteafterinstall

; === Sunshine engine ===
Source: "..\bundled\sunshine\sunshine.exe";      DestDir: "{tmp}\engine"; Flags: deleteafterinstall
Source: "..\bundled\sunshine\zlib1.dll";         DestDir: "{tmp}\engine"; Flags: deleteafterinstall
Source: "..\bundled\sunshine\assets\web\*";      DestDir: "{tmp}\web";    Flags: recursesubdirs deleteafterinstall
Source: "..\bundled\sunshine\assets\*";          DestDir: "{tmp}\sunshine_assets"; Flags: recursesubdirs deleteafterinstall
Source: "..\bundled\sunshine\scripts\*";         DestDir: "{tmp}\sunshine_scripts"; Flags: recursesubdirs deleteafterinstall
Source: "..\bundled\sunshine\tools\*";           DestDir: "{tmp}\sunshine_tools";   Flags: recursesubdirs deleteafterinstall

[Code]

// ============================================================
// AbortInstall: aborts with a clear message and restores backups
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
// InitializeSetup: pre-installation checks
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
// CurStepChanged: ssPostInstall - applies all fixes
// ============================================================
procedure CurStepChanged(CurStep: TSetupStep);
var
  DuoDir:       String;
  ResultCode:   Integer;
  StatusMsg:    String;
  RetryCount:   Integer;
  CopyResult:   Integer;
  SizeCheck:    Integer;
  CopySuccess: Boolean;
begin
  if CurStep = ssInstall then
  begin
    // Legacy cleanup (Pre-Install): ensures any old isolator service is removed from the system
    Exec('sc.exe', 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('sc.exe', 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  if CurStep <> ssPostInstall then Exit;

  DuoDir := ExpandConstant('{#DuoDir}');
  StatusMsg := '';
  CopyResult := 0;

  // ============================================================
  // Stop the service and kill any running DuoRdp.exe (with retry loop)
  // ============================================================
  StatusMsg := 'Stopping Duo Manager service...';
  Log(StatusMsg);

  // Retry loop: up to 3 attempts to stop service and kill process
  RetryCount := 0;
  while RetryCount < 3 do
  begin
    Exec('sc.exe', 'stop DuoService', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('taskkill.exe', '/f /im DuoRdp.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Wait and verify process is dead
    Exec('powershell.exe',
      '-NoProfile -Command "Start-Sleep -Milliseconds 500; ' +
      'if (Get-Process -Name DuoRdp -ErrorAction SilentlyContinue) { exit 1 } else { exit 0 }"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    if ResultCode = 0 then Break;  // Success - process is dead
    Inc(RetryCount);
    if RetryCount < 3 then
      Exec('powershell.exe', '-NoProfile -Command "Start-Sleep -Seconds 1"',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  if ResultCode <> 0 then
    Log('Warning: Could not stop DuoRdp.exe after 3 attempts. Will try to proceed anyway.');

  // ----------------------------------------------------------
  // Fix 1: DuoRdpWrapper (resolution intercept) - with retry
  // ----------------------------------------------------------
  Log('Applying resolution fix (DuoRdp.exe wrapper)...');

  // Ensure permissions
  Exec('takeown.exe', '/f "' + DuoDir + '\DuoRdp.exe" /a',                   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('icacls.exe',  '"' + DuoDir + '\DuoRdp.exe" /grant Administrators:F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Backup original
  if not FileExists(DuoDir + '\DuoRdp_orig.exe') then begin
    Exec('cmd.exe', '/c copy /y "' + DuoDir + '\DuoRdp.exe" "' + DuoDir + '\DuoRdp_orig.exe"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if (ResultCode <> 0) or (not FileExists(DuoDir + '\DuoRdp_orig.exe')) then
      AbortInstall('Resolution fix - backup of DuoRdp.exe',
        'Could not create backup DuoRdp_orig.exe (cmd copy returned ' + IntToStr(ResultCode) + ').',
        'Make sure Duo Manager service is stopped and try again.');
  end;

  // Copy with retry loop (up to 3 attempts)
  begin
    RetryCount := 0;
    SizeCheck := 0;
    CopySuccess := False;
    while (RetryCount < 3) and (not CopySuccess) do
    begin
      // Try cmd copy first
      Exec('cmd.exe', '/c copy /y "' + ExpandConstant('{tmp}\DuoRdpWrapper.exe') + '" "' + DuoDir + '\DuoRdp.exe"',
        '', SW_HIDE, ewWaitUntilTerminated, CopyResult);

      if CopyResult = 0 then
      begin
        // Verify file was actually replaced by comparing src and dst sizes.
        // Length > 0 alone would pass even if the old file was never overwritten.
        // Matching sizes proves the new binary (and only the new binary) is in place.
        Exec('powershell.exe',
          '-NoProfile -Command "$s=(Get-Item ''' + ExpandConstant('{tmp}\DuoRdpWrapper.exe') + ''').Length;' +
          '$d=(Get-Item ''' + DuoDir + '\DuoRdp.exe'').Length;if($s -eq $d){exit 0}else{exit 1}"',
          '', SW_HIDE, ewWaitUntilTerminated, SizeCheck);
        if SizeCheck = 0 then CopySuccess := True;
      end;

      if not CopySuccess then
      begin
        Log('Retry ' + IntToStr(RetryCount+1) + ': Copy failed (CopyResult=' + IntToStr(CopyResult) + ', SizeCheck=' + IntToStr(SizeCheck) + '), retrying...');
        Inc(RetryCount);
        if RetryCount < 3 then
        begin
          Exec('taskkill.exe', '/f /im DuoRdp.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
          Exec('powershell.exe', '-NoProfile -Command "Start-Sleep -Seconds 1"',
            '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
        end;
      end;
    end;

    if not CopySuccess then
      AbortInstall('Resolution fix - replacing DuoRdp.exe',
        'File size mismatch after copy — DuoRdp.exe was not replaced correctly.',
        'Run the installer again as Administrator. If the problem persists, download' + #13#10 +
        'DuoRdpWrapper.exe from the release page and copy it manually.');
  end;

  // ----------------------------------------------------------
  // Fix 2: Streaming engine (Sunshine)
  // ----------------------------------------------------------
  Exec('takeown.exe', '/f "' + DuoDir + '\sunshine.exe" /a',                   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('icacls.exe',  '"' + DuoDir + '\sunshine.exe" /grant Administrators:F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if not FileExists(DuoDir + '\sunshine_orig.exe') then begin
    Exec('cmd.exe', '/c copy /y "' + DuoDir + '\sunshine.exe" "' + DuoDir + '\sunshine_orig.exe"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if (ResultCode <> 0) or (not FileExists(DuoDir + '\sunshine_orig.exe')) then
      AbortInstall('Streaming engine - backup of sunshine.exe',
        'Could not create backup sunshine_orig.exe (cmd copy returned ' + IntToStr(ResultCode) + ').',
        'Make sure Duo Manager service is stopped and try again.');
  end;

  // Copy Sunshine engine
  Exec('cmd.exe', '/c copy /y "' + ExpandConstant('{tmp}\engine\sunshine.exe') + '" "' + DuoDir + '\sunshine.exe"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Exec('powershell.exe',
    '-NoProfile -Command "if ((Get-Item ''' + DuoDir + '\sunshine.exe'').Length -lt 10000000) { exit 1 } else { exit 0 }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode <> 0 then
    AbortInstall('Streaming engine - replacing sunshine.exe',
      'File is locked or could not be replaced.',
      'Open Services (services.msc), stop "Duo Manager", then run the installer again.');

  // Copy zlib1.dll and extra assets
  Exec('cmd.exe', '/c copy /y "' + ExpandConstant('{tmp}\engine\zlib1.dll') + '" "' + DuoDir + '\zlib1.dll"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Exec('takeown.exe', '/f "' + DuoDir + '\assets" /r /d y',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('icacls.exe', '"' + DuoDir + '\assets" /grant Administrators:F /t',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Exec('xcopy.exe',
    '"' + ExpandConstant('{tmp}\sunshine_assets\*') + '" "' + DuoDir + '\assets\" /Y /E /Q',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Exec('xcopy.exe',
    '"' + ExpandConstant('{tmp}\sunshine_scripts\*') + '" "' + DuoDir + '\scripts\" /Y /E /Q',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('xcopy.exe',
    '"' + ExpandConstant('{tmp}\sunshine_tools\*') + '" "' + DuoDir + '\tools\" /Y /E /Q',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // ----------------------------------------------------------
  // Fix 3: Web assets (management interface)
  // ----------------------------------------------------------
  Exec('takeown.exe', '/f "' + DuoDir + '\assets" /r /d y',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('icacls.exe', '"' + DuoDir + '\assets" /grant Administrators:F /t',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if not ForceDirectories(DuoDir + '\assets\web\assets') then
    AbortInstall('Web UI assets - creating directory',
      'Could not create: ' + DuoDir + '\assets\web\assets',
      'Check that you have Administrator rights and the Duo Manager folder is not read-only.');

  Exec('xcopy.exe',
    '"' + ExpandConstant('{tmp}\web\*') + '" "' + DuoDir + '\assets\web\" /Y /E /Q',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode <> 0 then
    AbortInstall('Web UI assets - copy',
      'xcopy failed with error code ' + IntToStr(ResultCode) + '.',
      'Check that ' + DuoDir + '\assets\web\ is writable and try again.');

  if not FileExists(DuoDir + '\assets\web\pin.html') then
    AbortInstall('Web UI assets - validation',
      'pin.html not found after copy.',
      'Antivirus may be blocking the files. Temporarily disable it and try again.');

  // ----------------------------------------------------------
  // Fix 4: Games_apps.json (only if it doesn't exist - preserves user customizations)
  // ----------------------------------------------------------
  if not ForceDirectories(DuoDir + '\config') then
    AbortInstall('App configuration - creating config directory',
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
      AbortInstall('App configuration - Games_apps.json',
        'Could not write to ' + DuoDir + '\config\Games_apps.json.',
        'Check that the config folder exists and is writable.');
  end;

  // ----------------------------------------------------------
  // Fix 5: Sunshine conf - enable debug logging to capture Moonlight resolution
  // Scans config/*.conf, skips duo_wrapper.conf, patches the most recently
  // modified conf containing min_log_level or log_path.
  // ----------------------------------------------------------
  Exec('powershell.exe',
    '-NoProfile -Command "' +
    '$dir = ''' + DuoDir + '\config''; ' +
    '$f = Get-ChildItem $dir -Filter *.conf | ' +
    '  Where-Object { $_.Name -ne ''duo_wrapper.conf'' } | ' +
    '  Where-Object { (Get-Content $_.FullName -Raw) -match ''min_log_level|log_path'' } | ' +
    '  Sort-Object LastWriteTime -Descending | ' +
    '  Select-Object -First 1 -ExpandProperty FullName; ' +
    'if ($f) { ' +
    '  $c = Get-Content $f -Raw; ' +
    '  $c = $c -replace ''min_log_level\s*=\s*\w+'', ''min_log_level = debug''; ' +
    '  $c = $c -replace ''virtual_sink\s*=\s*[^\r\n]*'', ''virtual_sink =''; ' +
    '  $c | Set-Content $f -NoNewline; ' +
    '} ' +
    '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // ----------------------------------------------------------
  // Fix 6: Duo.exe binary patch - zero out hardcoded virtual_sink config entry
  // Duo.exe has a config defaults table with "Remote Audio" as the default for
  // virtual_sink. It rewrites this on every session reconnect, breaking audio.
  // We zero the full 28-byte pattern (value + key name) so Duo skips the entry
  // entirely, leaving virtual_sink absent from Games.conf (null, not empty string).
  // Pattern: "Remote Audio\x00\x00\x00\x00virtual_sink" (unique in binary)
  // ----------------------------------------------------------
  Exec('takeown.exe', '/f "' + DuoDir + '\Duo.exe" /a',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('icacls.exe', '"' + DuoDir + '\Duo.exe" /grant Administrators:F',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if not FileExists(DuoDir + '\Duo_orig.exe') then begin
    Exec('cmd.exe', '/c copy /y "' + DuoDir + '\Duo.exe" "' + DuoDir + '\Duo_orig.exe"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if (ResultCode <> 0) or (not FileExists(DuoDir + '\Duo_orig.exe')) then
      AbortInstall('Duo.exe binary patch - backup',
        'Could not create backup Duo_orig.exe (cmd copy returned ' + IntToStr(ResultCode) + ').',
        'Make sure Duo Manager service is stopped and try again.');
  end;

  SaveStringToFile(ExpandConstant('{tmp}\patch_duo.ps1'),
    '$exe = ''' + DuoDir + '\Duo.exe'';' + #13#10 +
    '$bytes = [System.IO.File]::ReadAllBytes($exe);' + #13#10 +
    '$pattern = [byte[]](0x52,0x65,0x6D,0x6F,0x74,0x65,0x20,0x41,0x75,0x64,0x69,0x6F,0x00,0x00,0x00,0x00,0x76,0x69,0x72,0x74,0x75,0x61,0x6C,0x5F,0x73,0x69,0x6E,0x6B);' + #13#10 +
    '$idx = -1;' + #13#10 +
    'for ($i = 0; $i -le ($bytes.Length - $pattern.Length); $i++) {' + #13#10 +
    '  $ok = $true;' + #13#10 +
    '  for ($j = 0; $j -lt $pattern.Length; $j++) { if ($bytes[$i+$j] -ne $pattern[$j]) { $ok = $false; break } };' + #13#10 +
    '  if ($ok) { $idx = $i; break }' + #13#10 +
    '};' + #13#10 +
    'if ($idx -ge 0) { for ($k = 0; $k -lt $pattern.Length; $k++) { $bytes[$idx+$k] = 0 }; [System.IO.File]::WriteAllBytes($exe, $bytes); exit 0 } else { exit 1 }',
    False);

  Exec('powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\patch_duo.ps1') + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Fix 6 is optional — if the pattern is not found (different Duo version),
  // skip silently and let the rest of the installation complete normally.
  // The DuoRdpWrapper resolution fix still works without this patch.

  // ----------------------------------------------------------
  // Post-installation validation
  // ----------------------------------------------------------
  StatusMsg := 'Installation complete (Sunshine engine).' + #13#10 +
               'Component status:' + #13#10 + #13#10;

  Exec('powershell.exe',
    '-NoProfile -Command "if ((Get-Item ''' + DuoDir + '\DuoRdp.exe'').Length -lt 100000) { exit 0 } else { exit 1 }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode = 0 then
    StatusMsg := StatusMsg + '  [OK] Resolution wrapper (DuoRdp.exe replaced)' + #13#10
  else
    StatusMsg := StatusMsg + '  [!!] Resolution wrapper - DuoRdp.exe may not have been replaced' + #13#10;

  Exec('powershell.exe',
    '-NoProfile -Command "if ((Get-Item ''' + DuoDir + '\sunshine.exe'').Length -gt 10000000) { exit 0 } else { exit 1 }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode = 0 then
    StatusMsg := StatusMsg + '  [OK] Streaming engine (Sunshine sunshine.exe replaced)' + #13#10
  else
    StatusMsg := StatusMsg + '  [!!] Streaming engine - sunshine.exe may not have been replaced' + #13#10;

  if FileExists(DuoDir + '\assets\web\pin.html') then
    StatusMsg := StatusMsg + '  [OK] Web UI assets' + #13#10
  else
    StatusMsg := StatusMsg + '  [!!] Web UI assets - one or more files missing' + #13#10;

  if FileExists(DuoDir + '\Duo_orig.exe') then
    StatusMsg := StatusMsg + '  [OK] Duo.exe binary patch (virtual_sink default cleared)' + #13#10
  else
    StatusMsg := StatusMsg + '  [!!] Duo.exe binary patch - Duo_orig.exe backup not found' + #13#10;

  StatusMsg := StatusMsg + #13#10 + 'Connect from Moonlight and test.';

  MsgBox(StatusMsg, mbInformation, MB_OK);

end;


// ============================================================
// Uninstall
// ============================================================
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DuoDir:     String;
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    DuoDir := ExpandConstant('{#DuoDir}');

    // Stop the service to release files
    Exec('sc.exe', 'stop DuoService', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('powershell.exe', '-NoProfile -Command "Start-Sleep -Seconds 2"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Restores DuoRdp_orig.exe -> DuoRdp.exe (removes wrapper)
    if FileExists(DuoDir + '\DuoRdp_orig.exe') then
      Exec('cmd.exe', '/c copy /y "' + DuoDir + '\DuoRdp_orig.exe" "' + DuoDir + '\DuoRdp.exe"',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Restores sunshine_orig.exe -> sunshine.exe
    if FileExists(DuoDir + '\sunshine_orig.exe') then
      Exec('cmd.exe', '/c copy /y "' + DuoDir + '\sunshine_orig.exe" "' + DuoDir + '\sunshine.exe"',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Restores Duo_orig.exe -> Duo.exe (removes binary patch)
    if FileExists(DuoDir + '\Duo_orig.exe') then
      Exec('cmd.exe', '/c copy /y "' + DuoDir + '\Duo_orig.exe" "' + DuoDir + '\Duo.exe"',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Restores min_log_level = none in the active Sunshine conf
    Exec('powershell.exe',
      '-NoProfile -Command "' +
      '$dir = ''' + DuoDir + '\config''; ' +
      '$f = Get-ChildItem $dir -Filter *.conf | ' +
      '  Where-Object { $_.Name -ne ''duo_wrapper.conf'' } | ' +
      '  Where-Object { (Get-Content $_.FullName -Raw) -match ''min_log_level|log_path'' } | ' +
      '  Sort-Object LastWriteTime -Descending | ' +
      '  Select-Object -First 1 -ExpandProperty FullName; ' +
      'if ($f) { ' +
      '  $c = Get-Content $f -Raw; ' +
      '  $c = $c -replace ''min_log_level\s*=\s*\w+'', ''min_log_level = none''; ' +
      '  $c | Set-Content $f -NoNewline; ' +
      '} ' +
      '"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Service is NOT restarted automatically — user starts it manually.

    // Legacy cleanup: remove DuoGamepadIsolator if it exists
    Exec('sc.exe', 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('sc.exe', 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

end.
