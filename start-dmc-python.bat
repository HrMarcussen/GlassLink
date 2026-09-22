@echo off
rem Starts the Python DMC: the reference implementation and test tool. The default DMC is the .NET one (start-server.bat);
rem only one of them can run at a time (they share the DUs and the port).
cd /d "%~dp0"
".venv\Scripts\python.exe" -m glasslink serve %*
pause
