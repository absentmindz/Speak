; Speak complete application installer. Model weights are distributed separately.

#define MyAppName "Speak"
#define MyAppVersion "0.5.0"
#define MyAppPublisher "Hamza"
#define MyAppExeName "Speak.exe"

[Setup]
AppId={{D8A12B4C-1234-5678-ABCD-123456789ABC}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=artifacts
OutputBaseFilename=Speak-{#MyAppVersion}-Complete-Setup
Compression=lzma2/normal
SolidCompression=yes
DiskSpanning=no
WizardStyle=modern dark
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
CloseApplicationsFilter=Speak.exe
RestartApplications=no
ChangesEnvironment=no
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "autostart"; Description: "Start Speak when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "stage\App\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "stage\Prerequisites\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{autostartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: autostart

[Run]
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing the Microsoft Visual C++ runtime..."; Flags: waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
var
  ModelsPage: TInputDirWizardPage;

procedure InitializeWizard;
var
  ExistingModelsRoot: String;
begin
  ModelsPage := CreateInputDirPage(wpSelectDir,
    'Local AI models',
    'Choose where Speak should look for local model weights.',
    'The optional offline model pack can install into this folder. Existing compatible models are detected there after Speak starts.',
    False, '');
  ModelsPage.Add('Models folder:');

  if RegQueryStringValue(HKLM64, 'SOFTWARE\Speak', 'ModelsRoot', ExistingModelsRoot) then
    ModelsPage.Values[0] := ExistingModelsRoot
  else
    ModelsPage.Values[0] := ExpandConstant('{commonappdata}\Speak\Models');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = ModelsPage.ID) and (Trim(ModelsPage.Values[0]) = '') then
  begin
    MsgBox('Choose a folder for local AI models.', mbError, MB_OK);
    Result := False;
  end;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'ModelsRoot', ModelsPage.Values[0]);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    ForceDirectories(ModelsPage.Values[0]);
    RegWriteStringValue(HKLM64, 'SOFTWARE\Speak', 'ModelsRoot', ModelsPage.Values[0]);
  end;
end;
