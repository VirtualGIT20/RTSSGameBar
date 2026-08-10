@echo off
setlocal

where msbuild >nul 2>nul
if errorlevel 1 (
  echo MSBuild was not found. Run this from a Visual Studio Developer Command Prompt.
  exit /b 1
)

for %%I in ("%~dp0..") do set "ROOT=%%~fI"
set "OUT=%ROOT%\artifacts\AppxPackages"
if not exist "%OUT%" mkdir "%OUT%"

echo Packaging output directory:
echo   %OUT%
echo.

msbuild "%ROOT%\RTSSGameBar.sln" /restore /m ^
  /p:Configuration=Release ^
  /p:Platform=x64 ^
  /p:AppxPackageDir="%OUT%" ^
  /p:UapAppxPackageBuildMode=SideloadOnly ^
  /p:AppxBundle=Never ^
  /p:AppxPackageSigningEnabled=false

if errorlevel 1 exit /b %errorlevel%

echo.
echo Unsigned sideload package artifacts are under:
echo   %OUT%
echo Sign the generated .msix/.appx with a certificate whose Subject matches the manifest Publisher before installation.
