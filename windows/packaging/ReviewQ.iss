#ifndef AppVersion
  #define AppVersion "3.0.0"
#endif
#ifndef Architecture
  #define Architecture "x64"
#endif

[Setup]
AppId={{359056E8-A484-456B-80C2-C75902C70BC1}
AppName=ReviewQ
AppVersion={#AppVersion}
AppPublisher=ReviewQ contributors
AppPublisherURL=https://github.com/hritvikpatel1999/reviewq-win
DefaultDirName={localappdata}\Programs\ReviewQ
DefaultGroupName=ReviewQ
PrivilegesRequired=lowest
MinVersion=10.0.22621
#if Architecture == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#elif Architecture == "x64"
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
#elif Architecture == "x86"
ArchitecturesAllowed=x86compatible
#else
  #error Unsupported architecture
#endif
OutputDir={#OutputDir}
OutputBaseFilename=ReviewQ-{#AppVersion}-win-{#Architecture}-setup
SetupIconFile=ReviewQ.ico
UninstallDisplayIcon={app}\ReviewQ.exe
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
WizardStyle=modern
DisableProgramGroupPage=yes

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\ReviewQ"; Filename: "{app}\ReviewQ.exe"

[Run]
Filename: "{app}\ReviewQ.exe"; Description: "Open ReviewQ"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ReviewQ', Command) then
      if Pos(ExpandConstant('{app}\ReviewQ.exe'), Command) > 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ReviewQ');
  end;
end;
