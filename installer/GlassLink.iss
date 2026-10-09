; Inno Setup 6 script for the GlassLink DMC. Built by tools\build-release.ps1, which passes the version and build id:
;   ISCC.exe /DVersion=0.5.0 /DBuild=abc1234 installer\GlassLink.iss
; Installs the published folder dist\GlassLink-<version>\ into Program Files. The configuration and the logs live in
; %LOCALAPPDATA%\GlassLink and survive an uninstall. A running DMC is stopped gracefully (GlassLink.exe --quit) before
; its files are replaced, never killed.

#ifndef Version
  #define Version "0.0.0"
#endif
#ifndef Build
  #define Build "?"
#endif

[Setup]
AppId={{9E0C2A64-5B1F-4C77-9D3E-2B6A1C4F8D01}
AppName=GlassLink DMC
AppVersion={#Version}
AppVerName=GlassLink DMC {#Version}
AppPublisher=Thomas Marcussen
AppComments=Streams the simulator's cockpit displays to GlassLink display units. Build {#Build}.
DefaultDirName={autopf}\GlassLink
DefaultGroupName=GlassLink
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=GlassLink-{#Version}-setup
Compression=lzma2
SolidCompression=yes
; follows Windows' light or dark mode (Inno Setup 6.6 and later)
WizardStyle=modern dynamic
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
; The "start at sign-in" task writes the Run key of the user who runs the setup (on a one-person sim PC the same user).
UsedUserAreasWarning=no
UninstallDisplayIcon={app}\GlassLink.exe
MinVersion=10.0.19041
SetupIconFile=..\dotnet\src\GlassLink.Dmc\glasslink.ico

[Tasks]
; the sim starts GlassLink through its exe.xml and GlassLink quits with the sim; the same as the tray's "Start and stop
; with the simulator" (it needs the sim to have been started once on this PC; if not, the tray can do it later)
Name: "withsim"; Description: "Start GlassLink with the simulator, and stop it when the simulator quits (recommended)"
Name: "autostart"; Description: "Start GlassLink when I sign in to Windows (it then runs all the time)"; Flags: unchecked
Name: "firewall"; Description: "Let other devices on my network open the status page (a phone, a tablet: firewall rule for GlassLink.exe)"; Flags: unchecked

[Files]
Source: "..\dist\GlassLink-{#Version}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\GlassLink DMC"; Filename: "{app}\GlassLink.exe"; Comment: "Starts the DMC, or opens its status page when it is running"
Name: "{group}\Uninstall GlassLink DMC"; Filename: "{uninstallexe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "GlassLink DMC"; ValueData: """{app}\GlassLink.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
; delete first, then add: an upgrade must not pile up copies of the rule (#46)
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""GlassLink DMC"""; Flags: runhidden; Tasks: firewall
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""GlassLink DMC"" dir=in action=allow program=""{app}\GlassLink.exe"" enable=yes profile=private"; Flags: runhidden; Tasks: firewall
; the sim's exe.xml is in the profile of the user who runs the setup: as that user, not as the administrator
Filename: "{app}\GlassLink.exe"; Parameters: "--add-sim-entry"; Flags: runhidden runasoriginaluser; Tasks: withsim
; as the user who runs the setup, not with the setup's administrator rights
Filename: "{app}\GlassLink.exe"; Description: "Start the GlassLink DMC now"; Flags: nowait postinstall skipifsilent runasoriginaluser
; an update started from the DMC's status page (/update=1) is silent: start the new DMC again when it is done (#76),
; with --with-sim if the sim had started the old one (/withsim=1), so it still stops with the sim
Filename: "{app}\GlassLink.exe"; Parameters: "{code:RestartArgs}"; Flags: nowait runasoriginaluser; Check: IsUpdate

[UninstallRun]
Filename: "{app}\GlassLink.exe"; Parameters: "--quit"; Flags: runhidden; RunOnceId: "quit"
; "Start and stop with the simulator" may have been switched on from the tray: take the entry out of the sim's
; exe.xml, or the sim tries to start a deleted program at every start (#46)
Filename: "{app}\GlassLink.exe"; Parameters: "--remove-sim-entry"; Flags: runhidden; RunOnceId: "simentry"
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""GlassLink DMC"""; Flags: runhidden; RunOnceId: "firewall"

[Code]
function IsUpdate: Boolean;
begin
  Result := ExpandConstant('{param:update|0}') = '1';
end;

function RestartArgs(Param: String): String;
begin
  if ExpandConstant('{param:withsim|0}') = '1' then
    Result := '--with-sim'
  else
    Result := '';
end;

// "Start with Windows" may have been switched on from the tray, not by the installer's task: remove it either way (#46).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'GlassLink DMC');
end;

// A DMC that is running keeps GlassLink.exe and its files open: ask it to stop and wait (--quit returns when it has).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: String;
  Code: Integer;
begin
  Result := '';
  Exe := ExpandConstant('{app}\GlassLink.exe');
  if FileExists(Exe) then
  begin
    if not Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      Result := 'The GlassLink DMC that is running could not be stopped. Quit it from its icon in the notification area and run the setup again.';
  end;
end;
