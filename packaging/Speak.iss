; Speak application installer. Local AI runtimes and model weights are separate.

#define MyAppName "Speak"
#ifndef AppVersion
  #define AppVersion "0.5.1"
#endif
#define MyAppVersion AppVersion
#define MyAppPublisher "Speak contributors"
#define MyAppExeName "Speak.exe"

[Setup]
AppId={{D8A12B4C-1234-5678-ABCD-123456789ABC}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/absentmindz/Speak
AppSupportURL=https://github.com/absentmindz/Speak/issues
AppUpdatesURL=https://github.com/absentmindz/Speak/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=artifacts
OutputBaseFilename=Speak-{#MyAppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern dark
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\app.ico
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
CloseApplicationsFilter=Speak.exe
RestartApplications=no
ChangesEnvironment=no
MinVersion=10.0.17763
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "autostart"; Description: "Start Speak when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "stage\App\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{autostartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: autostart

[Run]
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
    'Choose where Speak should look for optional local model weights.',
    'You may install the separate offline model pack into this folder. Cloud features do not require it.',
    False, '');
  ModelsPage.Add('Models folder:');

  if RegQueryStringValue(HKCU, 'SOFTWARE\Speak', 'ModelsRoot', ExistingModelsRoot) then
    ModelsPage.Values[0] := ExistingModelsRoot
  else if RegQueryStringValue(HKLM64, 'SOFTWARE\Speak', 'ModelsRoot', ExistingModelsRoot) then
    ModelsPage.Values[0] := ExistingModelsRoot
  else
    ModelsPage.Values[0] := ExpandConstant('{localappdata}\Speak\Models');
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
    RegWriteStringValue(HKCU, 'SOFTWARE\Speak', 'ModelsRoot', ModelsPage.Values[0]);
  end;
end;
