@echo off
setlocal
net session >nul 2>&1 || (echo Execute como Administrador. & pause & exit /b 1)

echo.
echo === Deploy DuoGamepadIsolator ===
echo.

:: 1. Compilar
call "%~dp0build.bat"
if errorlevel 1 ( echo ERRO na compilacao! & exit /b 1 )

:: 2. Parar servico
echo [3/6] Parando servico...
sc query DuoGamepadIsolator >nul 2>&1
if not errorlevel 1 (
    sc stop DuoGamepadIsolator >nul 2>&1
    :: Espera para liberar handles
    timeout /t 3 /nobreak >nul
)

:: 3. Garante que qualquer instancia foi terminada
taskkill /f /im DuoGamepadIsolator.exe >nul 2>&1

:: 4. Substituir binario no destino final (DuoFix)
echo [4/6] Substituindo binario...
set DUOFIX=C:\Program Files\DuoFix
if not exist "%DUOFIX%" mkdir "%DUOFIX%"
copy /y "%~dp0..\bin\DuoGamepadIsolator.exe" "%DUOFIX%\DuoGamepadIsolator.exe" >nul

:: 5. Instalar/Configurar Servico
echo [5/6] Configurando Servico e Recovery...
sc query DuoGamepadIsolator >nul 2>&1
if errorlevel 1 (
    "%DUOFIX%\DuoGamepadIsolator.exe" --install
) else (
    :: Servico ja existe, atualizar config de recovery e start mode (boot manual nao suportado sem driver)
    sc config DuoGamepadIsolator start= auto >nul
    sc failure DuoGamepadIsolator reset= 86400 actions= restart/5000/restart/10000/restart/30000 >nul
)

:: 6. Iniciar servico
echo [6/6] Iniciando servico...
sc start DuoGamepadIsolator >nul

echo.
echo === DEPLOY CONCLUIDO ===
sc query DuoGamepadIsolator | findstr "ESTADO"
echo.
pause
