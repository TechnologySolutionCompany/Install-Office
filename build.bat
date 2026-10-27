@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1"
if errorlevel 1 (
  echo Falha ao gerar o aplicativo. Verifique as mensagens acima.
  pause
  exit /b 1
)
pause
