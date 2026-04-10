@echo off
setlocal
echo === Duo Manager Fix — Build Script ===
echo.

:: Encontrar csc.exe
set CSC=
for /r "C:\Windows\Microsoft.NET\Framework64" %%f in (csc.exe) do set CSC=%%f
if "%CSC%"=="" (
    echo ERRO: csc.exe nao encontrado. Instale o .NET Framework.
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
    /reference:System.Security.dll
if errorlevel 1 ( echo ERRO: DuoGamepadIsolator falhou. & exit /b 1 )
echo OK: DuoGamepadIsolator.exe

echo.
echo Build concluido. Binarios em: %~dp0..\bin\
