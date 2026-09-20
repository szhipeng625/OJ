@echo off
setlocal
set ROOT=%~dp0
set MSB="D:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"

echo ============================================
echo  OJ One-Click Build  (Release x64)
echo ============================================

echo [1/3] Building C++ core: lsm_shared + ojcore ...
%MSB% "%ROOT%client\ojcore\ojcore.sln" /p:Configuration=Release /p:Platform=x64 /m /v:minimal
if errorlevel 1 (echo [FAIL] C++ core build failed & exit /b 1)
echo [OK] C++ core built

echo [2/3] Publishing WPF client to %ROOT%dist ...
if exist "%ROOT%dist" rmdir /s /q "%ROOT%dist"
cd /d "%ROOT%client"
dotnet publish client.csproj -c Release -o "%ROOT%dist" --nologo
if errorlevel 1 (echo [FAIL] WPF client publish failed & exit /b 1)

echo [3/3] Verifying outputs ...
if not exist "%ROOT%dist\client.exe" (echo [FAIL] client.exe missing & exit /b 1)
if not exist "%ROOT%dist\ojcore.dll" (echo [FAIL] ojcore.dll missing & exit /b 1)
if not exist "%ROOT%dist\lsm_shared.dll" (echo [FAIL] lsm_shared.dll missing & exit /b 1)
if not exist "%ROOT%dist\HandyControl.dll" (echo [FAIL] HandyControl.dll missing & exit /b 1)

echo.
echo ============================================
echo  Build complete: %ROOT%dist
echo  Run: double-click dist\client.exe
echo ============================================
dir /b "%ROOT%dist"
exit /b 0
