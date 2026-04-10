@echo off
:: Instalacao manual — requer privilegios de Administrador
net session >nul 2>&1 || (echo Execute como Administrador. & pause & exit /b 1)

setlocal
set BIN=%~dp0..\bin
set DUO=C:\Program Files\Duo
set APOLLO=C:\Program Files\Apollo
set DUOFIX=C:\Program Files\DuoFix

echo === Duo Manager Fix — Instalacao Manual ===
echo.

:: Verificar pre-requisitos
if not exist "%DUO%\Duo.exe" (
    echo ERRO: Duo Manager nao encontrado em "%DUO%"
    echo Instale o Duo Manager 1.5.6 primeiro.
    pause & exit /b 1
)
if not exist "%APOLLO%\sunshine.exe" (
    echo ERRO: Apollo nao encontrado em "%APOLLO%"
    echo Instale o Apollo 0.4.6 primeiro.
    pause & exit /b 1
)
if not exist "%BIN%\DuoRdpWrapper.exe" (
    echo ERRO: Binarios nao encontrados. Execute scripts\build.bat primeiro.
    pause & exit /b 1
)

echo [1/5] Instalando DuoRdpWrapper (fix de resolucao)...
takeown /f "%DUO%\DuoRdp.exe" /a >nul 2>&1
icacls "%DUO%\DuoRdp.exe" /grant Administrators:F >nul
if not exist "%DUO%\DuoRdp_orig.exe" (
    copy /y "%DUO%\DuoRdp.exe" "%DUO%\DuoRdp_orig.exe" >nul
    echo   Backup: DuoRdp_orig.exe
)
copy /y "%BIN%\DuoRdpWrapper.exe" "%DUO%\DuoRdp.exe" >nul
echo   OK: DuoRdp.exe substituido.

echo [2/5] Instalando Apollo sunshine.exe...
takeown /f "%DUO%\sunshine.exe" /a >nul 2>&1
icacls "%DUO%\sunshine.exe" /grant Administrators:F >nul
if not exist "%DUO%\sunshine_orig.exe" (
    copy /y "%DUO%\sunshine.exe" "%DUO%\sunshine_orig.exe" >nul
    echo   Backup: sunshine_orig.exe
)
copy /y "%APOLLO%\sunshine.exe" "%DUO%\sunshine.exe" >nul
echo   OK: sunshine.exe substituido.

echo [3/5] Copiando web assets do Apollo...
if not exist "%DUO%\assets\web\assets" mkdir "%DUO%\assets\web\assets"
copy /y "%APOLLO%\assets\web\pin.html"     "%DUO%\assets\web\pin.html"     >nul
copy /y "%APOLLO%\assets\web\login.html"   "%DUO%\assets\web\login.html"   >nul
copy /y "%APOLLO%\assets\web\welcome.html" "%DUO%\assets\web\welcome.html" >nul
xcopy /y /e /q "%APOLLO%\assets\web\assets\*" "%DUO%\assets\web\assets\" >nul
echo   OK: Web assets copiados.

echo [4/5] Instalando DuoGamepadIsolator...
if not exist "%DUOFIX%" mkdir "%DUOFIX%"
copy /y "%BIN%\DuoGamepadIsolator.exe" "%DUOFIX%\DuoGamepadIsolator.exe" >nul
sc query DuoGamepadIsolator >nul 2>&1 && sc stop DuoGamepadIsolator >nul 2>&1 && sc delete DuoGamepadIsolator >nul 2>&1
"%DUOFIX%\DuoGamepadIsolator.exe" --install
echo   OK: Servico instalado e iniciado.

echo [5/5] Configurando Games_apps.json...
echo {"apps":[{"uuid":"3C56B52A-50C8-3601-CC0D-042310A47F60","image-path":"desktop.png","name":"Desktop","prep-cmd":[]},{"uuid":"B27218EA-7DEB-C42F-AC87-A7A4CB305671","name":"Steam Big Picture","cmd":"steam://open/bigpicture","wait-all":true,"auto-detach":true,"image-path":"steam.png"}],"env":{},"version":2} > "%DUO%\config\Games_apps.json"
echo   OK: Apps configurados (Desktop + Steam Big Picture).

echo.
echo =============================================
echo  Instalacao concluida!
echo  Log do isolador: C:\Users\Public\duo_isolator.log
echo  Conecte pelo Moonlight e teste.
echo =============================================
pause
