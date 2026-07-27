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
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=artifacts
OutputBaseFilename=Speak-{#MyAppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern dark
PrivilegesRequired=admin
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
; Preserve an existing machine's runtime/model configuration during upgrades.
; New installations receive the audited portable defaults.
Source: "stage\App\*"; DestDir: "{app}"; Excludes: "appsettings.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "stage\App\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Check: ShouldCreateProgramsShortcut
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{autostartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Early custom Speak 0.5 installers created these shortcuts outside Inno's
; tracking. They continue to point at the same machine-wide app after upgrade,
; and are removed when the replacement is uninstalled.
Type: files; Name: "{commondesktop}\{#MyAppName}.lnk"
Type: files; Name: "{commonprograms}\{#MyAppName}\{#MyAppName}.lnk"
Type: dirifempty; Name: "{commonprograms}\{#MyAppName}"
Type: files; Name: "{app}\uninstall.ps1"
Type: files; Name: "{app}\uninstall.bat"

[Code]
var
  ModelsPage: TInputDirWizardPage;

function HasQwenModel(const RootPath: String;
  const DirectoryName: String): Boolean;
var
  ModelPath: String;
begin
  ModelPath := AddBackslash(RootPath) + DirectoryName;
  Result :=
    FileExists(AddBackslash(ModelPath) + 'config.json') and
    (FileExists(AddBackslash(ModelPath) + 'model.safetensors') or
     FileExists(AddBackslash(ModelPath) +
       'model.safetensors.index.json'));
end;

function HasKnownModelLayout(const RootPath: String): Boolean;
var
  NormalizedRoot: String;
begin
  NormalizedRoot := RemoveBackslashUnlessRoot(Trim(RootPath));
  Result :=
    (NormalizedRoot <> '') and
    DirExists(NormalizedRoot) and
    (FileExists(AddBackslash(NormalizedRoot) +
       'whisper\large-v3.pt') or
     HasQwenModel(NormalizedRoot,
       'Qwen3-TTS-12Hz-1.7B-CustomVoice') or
     HasQwenModel(NormalizedRoot,
       'Qwen3-TTS-12Hz-1.7B-Base'));
end;

function ShouldCreateProgramsShortcut(): Boolean;
begin
  { A custom Speak 0.5 installer used a Start Menu group rather than the
    direct link used by Inno. Keep that working link during upgrade and avoid
    creating a duplicate; [UninstallDelete] adopts its cleanup. }
  Result := not FileExists(ExpandConstant(
    '{commonprograms}\{#MyAppName}\{#MyAppName}.lnk'));
end;

procedure InitializeWizard;
var
  CurrentUserModelsRoot: String;
  LocalMachineModelsRoot: String;
begin
  ModelsPage := CreateInputDirPage(wpSelectDir,
    'Local AI models',
    'Choose where Speak should look for optional local model weights.',
    'You may install the separate offline model pack into this folder. Cloud features do not require it.',
    False, '');
  ModelsPage.Add('Models folder:');

  CurrentUserModelsRoot := '';
  LocalMachineModelsRoot := '';
  RegQueryStringValue(
    HKCU, 'SOFTWARE\Speak', 'ModelsRoot', CurrentUserModelsRoot);
  RegQueryStringValue(
    HKLM64, 'SOFTWARE\Speak', 'ModelsRoot', LocalMachineModelsRoot);

  { Mirror Speak's runtime precedence: a valid per-user root, then a valid
    machine root, then the first configured non-empty fallback. }
  if HasKnownModelLayout(CurrentUserModelsRoot) then
    ModelsPage.Values[0] := CurrentUserModelsRoot
  else if HasKnownModelLayout(LocalMachineModelsRoot) then
    ModelsPage.Values[0] := LocalMachineModelsRoot
  else if Trim(CurrentUserModelsRoot) <> '' then
    ModelsPage.Values[0] := CurrentUserModelsRoot
  else if Trim(LocalMachineModelsRoot) <> '' then
    ModelsPage.Values[0] := LocalMachineModelsRoot
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
var
  LegacyDisplayName: String;
  LegacyInstallLocation: String;
  LegacyUninstallString: String;
  LegacyUninstallKey: String;
begin
  if CurStep = ssPostInstall then
  begin
    ForceDirectories(ModelsPage.Values[0]);
    RegWriteStringValue(HKLM64, 'SOFTWARE\Speak', 'ModelsRoot', ModelsPage.Values[0]);
    RegWriteStringValue(HKCU, 'SOFTWARE\Speak', 'ModelsRoot', ModelsPage.Values[0]);

    { Some early Speak 0.5 installers used a custom, non-Inno uninstall
      registration. Remove only that exact stale registration after the
      machine-wide replacement has completed successfully. }
    LegacyUninstallKey :=
      'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Speak';
    if RegQueryStringValue(
         HKLM64, LegacyUninstallKey, 'DisplayName', LegacyDisplayName) and
       RegQueryStringValue(
         HKLM64, LegacyUninstallKey, 'InstallLocation', LegacyInstallLocation) and
       RegQueryStringValue(
         HKLM64, LegacyUninstallKey, 'UninstallString', LegacyUninstallString) and
       (CompareText(Trim(LegacyDisplayName), '{#MyAppName}') = 0) and
       (CompareText(
          RemoveBackslashUnlessRoot(Trim(LegacyInstallLocation)),
          RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) = 0) and
       (Pos('uninstall.ps1', Lowercase(LegacyUninstallString)) > 0) then
    begin
      RegDeleteKeyIncludingSubkeys(HKLM64, LegacyUninstallKey);
      DeleteFile(ExpandConstant('{app}\uninstall.ps1'));
      DeleteFile(ExpandConstant('{app}\uninstall.bat'));
    end;
  end;
end;
