@echo off
setlocal
echo === Duo Manager Fix — Build Script ===
echo.

:: Encontrar csc.exe (path direto evita pegar WPF\pt-BR\csc.exe)
set CSC=
if exist "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" (
    set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
) else if exist "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe" (
    set "CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
if "%CSC%"=="" (
    echo ERRO: csc.exe nao encontrado. Instale o .NET Framework 4.x.
    exit /b 1
)
echo Compilador: %CSC%
echo.

:: Criar pasta bin
if not exist "..\bin" mkdir ..\bin

:: Compilar DuoRdpWrapper
echo [1/2] Compilando DuoRdpWrapper...
"%CSC%" /out:..\bin\DuoRdpWrapper.exe ..\src\DuoRdpWrapper.cs
if errorlevel 1 ( echo ERRO: DuoRdpWrapper falhou. & exit /b 1 )
echo OK: DuoRdpWrapper.exe

:: Compilar DuoGamepadIsolator
echo [2/2] Compilando DuoGamepadIsolator...
"%CSC%" /out:..\bin\DuoGamepadIsolator.exe ..\src\DuoGamepadIsolator.cs ^
    /reference:System.ServiceProcess.dll ^
    /reference:System.Security.dll ^
    /platform:anycpu /optimize+
if errorlevel 1 ( echo ERRO: DuoGamepadIsolator falhou. & exit /b 1 )
echo OK: DuoGamepadIsolator.exe

echo.
echo Build concluido. Binarios em: %~dp0..\bin\
