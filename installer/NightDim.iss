[Setup]
AppId=NightDim-4660BA9A-2855-42BD-926A-E897FC391862
AppName=NightDim
AppVersion={#NightDimVersion}
AppPublisher=NightDim
AppPublisherURL=https://github.com/dFAiDGAAS529-source/NightDim
AppSupportURL=https://github.com/dFAiDGAAS529-source/NightDim/issues
AppUpdatesURL=https://github.com/dFAiDGAAS529-source/NightDim/releases
DefaultDirName={localappdata}\Programs\NightDim
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
AppMutex=Local\NightDim.ScreenEffects
CloseApplications=no
RestartApplications=no
SetupIconFile={#NightDimIcon}
UninstallDisplayIcon={app}\NightDim.exe
VersionInfoVersion={#NightDimVersion}
VersionInfoDescription=NightDim Setup
WizardStyle=modern dynamic
Compression=lzma2
SolidCompression=yes
OutputDir={#NightDimOutput}
OutputBaseFilename=NightDim-Setup-{#NightDimVersion}-win-x64

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Messages]
en.SetupAppRunningError=NightDim is running. Use Exit in its tray menu to restore your display, then click OK to continue.
ru.SetupAppRunningError=NightDim запущен. Выберите «Выход» в меню значка NightDim в трее, чтобы восстановить экран, затем нажмите OK.
en.UninstallAppRunningError=NightDim is running. Use Exit in its tray menu to restore your display, then click OK to continue.
ru.UninstallAppRunningError=NightDim запущен. Выберите «Выход» в меню значка NightDim в трее, чтобы восстановить экран, затем нажмите OK.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#NightDimPayload}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\NightDim"; Filename: "{app}\NightDim.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\NightDim"; Filename: "{app}\NightDim.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\NightDim.exe"; Description: "{cm:LaunchProgram,NightDim}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValue = 'NightDim';

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
  InstalledCommand: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    InstalledCommand := '"' + ExpandConstant('{app}\NightDim.exe') + '" --autostart';
    if RegQueryStringValue(HKCU, RunKey, RunValue, Command) and
       (CompareText(Command, InstalledCommand) = 0) then
      RegDeleteValue(HKCU, RunKey, RunValue);
  end;
end;
