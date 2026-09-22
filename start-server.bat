@echo off
rem Starts the GlassLink DMC (.NET, tray icon): builds it first if needed. Only one DMC can run at a time; the Python
rem reference DMC is start-dmc-python.bat.
cd /d "%~dp0"
if not exist "dotnet\src\GlassLink.Dmc\bin\Release\net10.0-windows10.0.26100.0\GlassLink.exe" dotnet build dotnet\GlassLink.slnx -c Release -nologo -v q
start "" "dotnet\src\GlassLink.Dmc\bin\Release\net10.0-windows10.0.26100.0\GlassLink.exe" --config "%~dp0config.json"
