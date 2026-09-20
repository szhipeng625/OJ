@echo off
chcp 65001 >nul
REM OJ 一键构建打包（等价于 scripts\build.ps1 -All -Publish）
REM 可追加参数，例如：build_all.bat -Configuration Debug
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1" -All -Publish %*
exit /b %ERRORLEVEL%
