@echo off
:: prepare_bundle.bat — Prepara os assets de ambas as engines (Apollo e Sunshine).
:: Execute UMA VEZ antes de compilar o setup.iss no Inno Setup.
::
:: Estrutura gerada:
::   bundled\apollo\sunshine.exe        (Apollo 0.4.6)
::   bundled\apollo\web\               (Web UI do Apollo)
::   bundled\sunshine\sunshine.exe      (Sunshine nativo)
::   bundled\sunshine\zlib1.dll
::   bundled\sunshine\assets\           (Web UI + configs)
::   bundled\sunshine\scripts\
::   bundled\sunshine\tools\

setlocal

set APOLLO=C:\Program Files\Apollo
set SUNSHINE=C:\Program Files\Sunshine\Sunshine
set OUT=%~dp0..\bundled

echo === Duo Manager Fix — Preparar Bundle (Dual Engine) ===
echo.

:: ============================================================
:: Apollo 0.4.6
:: ============================================================
echo [Apollo] Verificando...
if not exist "%APOLLO%\sunshine.exe" (
    echo   AVISO: Apollo nao encontrado em "%APOLLO%"
    echo   O installer funcionara apenas com Sunshine nativo.
    echo   Se precisar do Apollo: https://github.com/SudoMaker/Apollo/releases/tag/0.4.6
    echo.
) else (
    echo   Encontrado. Copiando...
    if not exist "%OUT%\apollo\web\assets" mkdir "%OUT%\apollo\web\assets"
    copy /y "%APOLLO%\sunshine.exe"                "%OUT%\apollo\sunshine.exe" >nul
    copy /y "%APOLLO%\assets\web\pin.html"         "%OUT%\apollo\web\pin.html" >nul
    copy /y "%APOLLO%\assets\web\login.html"       "%OUT%\apollo\web\login.html" >nul
    copy /y "%APOLLO%\assets\web\welcome.html"     "%OUT%\apollo\web\welcome.html" >nul
    xcopy /y /e /q "%APOLLO%\assets\web\assets\*"  "%OUT%\apollo\web\assets\" >nul
    echo   [OK] Apollo bundled.
    echo.
)

:: ============================================================
:: Sunshine Nativo
:: ============================================================
echo [Sunshine] Verificando...
if not exist "%SUNSHINE%\sunshine.exe" (
    echo   AVISO: Sunshine nao encontrado em "%SUNSHINE%"
    echo   O installer funcionara apenas com Apollo.
    echo   Download: https://github.com/LizardByte/Sunshine/releases
    echo.
) else (
    echo   Encontrado. Copiando...
    if not exist "%OUT%\sunshine\assets" mkdir "%OUT%\sunshine\assets"
    if not exist "%OUT%\sunshine\scripts" mkdir "%OUT%\sunshine\scripts"
    if not exist "%OUT%\sunshine\tools" mkdir "%OUT%\sunshine\tools"

    copy /y "%SUNSHINE%\sunshine.exe"  "%OUT%\sunshine\sunshine.exe" >nul
    copy /y "%SUNSHINE%\zlib1.dll"     "%OUT%\sunshine\zlib1.dll" >nul
    xcopy /y /e /q "%SUNSHINE%\assets\*"  "%OUT%\sunshine\assets\" >nul
    xcopy /y /e /q "%SUNSHINE%\scripts\*" "%OUT%\sunshine\scripts\" >nul
    xcopy /y /e /q "%SUNSHINE%\tools\*"   "%OUT%\sunshine\tools\" >nul
    echo   [OK] Sunshine bundled.
    echo.
)

:: ============================================================
:: Validacao
:: ============================================================
set HAS_APOLLO=0
set HAS_SUNSHINE=0
if exist "%OUT%\apollo\sunshine.exe" set HAS_APOLLO=1
if exist "%OUT%\sunshine\sunshine.exe" set HAS_SUNSHINE=1

if %HAS_APOLLO%==0 if %HAS_SUNSHINE%==0 (
    echo ERRO: Nenhuma engine encontrada. Instale Apollo ou Sunshine e rode novamente.
    pause & exit /b 1
)

echo === Resumo ===
if %HAS_APOLLO%==1 (echo   [OK] Apollo 0.4.6) else (echo   [--] Apollo nao disponivel)
if %HAS_SUNSHINE%==1 (echo   [OK] Sunshine nativo) else (echo   [--] Sunshine nao disponivel)
echo.
echo Bundle pronto em: %OUT%
echo Agora compile o installer\setup.iss no Inno Setup (F9).
pause
