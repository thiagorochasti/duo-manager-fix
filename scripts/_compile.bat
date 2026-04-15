@echo off
C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /out:"%~dp0..\bin\DuoGamepadIsolator.exe" "%~dp0..\src\DuoGamepadIsolator.cs" /reference:System.ServiceProcess.dll /reference:System.Security.dll
if errorlevel 1 (
    echo COMPILE FAILED
    exit /b 1
)
echo COMPILE OK
