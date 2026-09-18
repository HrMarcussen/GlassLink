@echo off
rem Start the GlassLink server (captures the pop-outs listed in config.json and serves them on port 8765).
cd /d "%~dp0"
".venv\Scripts\python.exe" -m glasslink serve %*
pause
