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
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\GlassLink.exe
MinVersion=10.0.19041
SetupIconFile=..\dotnet\src\GlassLink.Dmc\glasslink.ico

[Tasks]
Name: "autostart"; Description: "Start the DMC when I sign in to Windows"; Flags: unchecked
Name: "firewall"; Description: "Let other devices on my network open the status page (a phone, a tablet: firewall rule for GlassLink.exe)"; Flags: unchecked

[Files]
Source: "..\dist\GlassLink-{#Version}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\GlassLink DMC"; Filename: "{app}\GlassLink.exe"; Comment: "Starts the DMC, or opens its status page when it is running"
Name: "{group}\Uninstall GlassLink DMC"; Filename: "{uninstallexe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "GlassLink DMC"; ValueData: """{app}\GlassLink.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""GlassLink DMC"" dir=in action=allow program=""{app}\GlassLink.exe"" enable=yes profile=private"; Flags: runhidden; Tasks: firewall
Filename: "{app}\GlassLink.exe"; Description: "Start the GlassLink DMC now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\GlassLink.exe"; Parameters: "--quit"; Flags: runhidden; RunOnceId: "quit"
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""GlassLink DMC"""; Flags: runhidden; RunOnceId: "firewall"

[Code]
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
