@echo off
rem build.bat: x64 into bin\x64\BusyBunny.exe. build.bat full: arm64 and x64, as the release is built.
cd /d "%~dp0"
set ARCHES=x64
if /i "%1"=="full" set ARCHES=arm64 x64
for %%a in (%ARCHES%) do (
  dotnet publish BusyBunny.csproj -c Release -r win-%%a -o bin\%%a -nologo -v q || exit /b 1
)
echo Built: %ARCHES%
