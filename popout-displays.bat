@echo off
rem Recreate the MSFS pop-outs (Right-Alt + click on PFD, ND, upper and lower ECAM) and park them off-screen.
rem Run this once per sim session, with the sim in the cockpit view, before or after start-dmc-python.bat (the .NET DMC pops the displays out by itself).
cd /d "%~dp0"
".venv\Scripts\python.exe" tools\auto_popout.py %*
pause
