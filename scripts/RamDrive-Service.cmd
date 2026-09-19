@echo off
rem ============================================================================
rem  RamDrive-Service — double-click launcher for RamDrive-Service.ps1
rem
rem  Registering the service needs administrator rights, so Windows shows a UAC
rem  prompt. The elevated window then asks what to do; press Enter for install.
rem
rem      I = install    register + start the service for THIS folder (default)
rem      U = uninstall  stop + remove the service (the RAM disk is unmounted and
rem                     everything on it is lost - it asks for confirmation)
rem      R = restart    stop + start the service
rem      S = status     show what is registered
rem
rem  The elevated window stays open afterwards, so the output stays readable.
rem
rem  Command line (no menu):
rem      RamDrive-Service.cmd status
rem      RamDrive-Service.cmd install
rem      RamDrive-Service.cmd uninstall
rem ============================================================================
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0RamDrive-Service.ps1" %*
if errorlevel 1 (
    echo.
    echo Failed with exit code %errorlevel%.
    pause
)
endlocal
