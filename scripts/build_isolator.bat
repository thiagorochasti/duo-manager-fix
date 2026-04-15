@echo off
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
set "SRC=%~dp0..\src\DuoGamepadIsolator.cs"
set "OUT=%~dp0..\bin\DuoGamepadIsolator.exe"
"%CSC%" "/out:%OUT%" "%SRC%" /reference:System.ServiceProcess.dll /reference:System.Security.dll
if errorlevel 1 ( echo COMPILE FAILED & exit /b 1 )
echo OK: DuoGamepadIsolator.exe compiled.
