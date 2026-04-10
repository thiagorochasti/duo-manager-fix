@echo off
:: prepare_bundle.bat — Extrai os arquivos do Apollo necessarios para o instalador.
:: Execute UMA VEZ antes de compilar o setup.iss no Inno Setup.
:: Requer o Apollo 0.4.6 instalado em C:\Program Files\Apollo\

setlocal
set APOLLO=C:\Program Files\Apollo
set OUT=%~dp0..\bundled

echo === Duo Manager Fix — Preparar Bundle ===
echo.

if not exist "%APOLLO%\sunshine.exe" (
    echo ERRO: Apollo nao encontrado em "%APOLLO%"
    echo Instale o Apollo 0.4.6 e rode este script novamente.
    echo Download: https://github.com/SudoMaker/Apollo/releases/tag/0.4.6
    pause & exit /b 1
)

echo Criando pasta bundled...
if not exist "%OUT%\web\assets" mkdir "%OUT%\web\assets"

echo [1/2] Copiando sunshine.exe...
copy /y "%APOLLO%\sunshine.exe" "%OUT%\sunshine.exe" >nul
echo   OK

echo [2/2] Copiando web assets...
copy /y "%APOLLO%\assets\web\pin.html"     "%OUT%\web\pin.html"     >nul
copy /y "%APOLLO%\assets\web\login.html"   "%OUT%\web\login.html"   >nul
copy /y "%APOLLO%\assets\web\welcome.html" "%OUT%\web\welcome.html" >nul
xcopy /y /e /q "%APOLLO%\assets\web\assets\*" "%OUT%\web\assets\" >nul
echo   OK

echo.
echo Bundle pronto em: %OUT%
echo Agora compile o installer\setup.iss no Inno Setup (F9).
pause
