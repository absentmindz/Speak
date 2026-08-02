; Speak complete offline installer: self-contained .NET app, app-local CPU Whisper runtime,
; FFmpeg payload, VC++ redist, and the audited Whisper base model in one Setup EXE.

#ifdef TestMode
  #define MyAppName "Speak Complete Installer Test"
  #define MyAppId "{{B61EC1D3-60EC-4A0E-8C6F-20DCA9636D68}"
  #define SpeakRegistrySubkey "SOFTWARE\SpeakCompleteInstallerTest"
  #define CloseApplicationsSetting "no"
  #define DefaultDirSetting "{localappdata}\Programs\Speak Complete Installer Test"
  #define PrivilegesRequiredSetting "lowest"
#else
  #define MyAppName "Speak"
  #define MyAppId "{{D8A12B4C-1234-5678-ABCD-123456789ABC}"
  #define SpeakRegistrySubkey "SOFTWARE\Speak"
  #define CloseApplicationsSetting "yes"
  #define DefaultDirSetting "{autopf}\Speak"
  #define PrivilegesRequiredSetting "admin"
#endif
#ifndef CompleteStageRoot
  #define CompleteStageRoot "stage\\complete"
#endif
#ifndef CompleteArtifactsRoot
  #define CompleteArtifactsRoot "artifacts\\complete"
#endif
#ifndef AppVersion
  #define AppVersion "0.5.3"
#endif
#define MyAppVersion AppVersion
#define MyAppPublisher "Speak contributors"
#define MyAppExeName "Speak.exe"
#define BaseModelSha256 "ed3a0b6b1c0edf879ad9b11b1af5a0e6ab5db9205f891f668f8b0e6c6326e34e"
#define VcRedistMinimumVersion "14.51.36247.0"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion} Complete Offline
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/absentmindz/Speak
AppSupportURL=https://github.com/absentmindz/Speak/issues
AppUpdatesURL=https://github.com/absentmindz/Speak/releases
DefaultDirName={#DefaultDirSetting}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#CompleteArtifactsRoot}
OutputBaseFilename=Speak-{#MyAppVersion}-Complete-Offline-Setup
Compression=lzma2/max
SolidCompression=yes
DiskSpanning=no
WizardStyle=modern dark
PrivilegesRequired={#PrivilegesRequiredSetting}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\app.ico
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications={#CloseApplicationsSetting}
CloseApplicationsFilter=Speak.exe
RestartApplications=no
ChangesEnvironment=no
MinVersion=10.0.17763
SetupLogging=yes
VersionInfoVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "autostart"; Description: "Start Speak when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[InstallDelete]
; Replace Speak's installer-owned embedded runtimes so upgrades cannot retain
; stale Python packages or keep the standard install's dedicated Edge runtime
; ahead of this complete installer's shared Whisper/Edge runtime. External
; models and user data are untouched.
Type: filesandordirs; Name: "{app}\Runtime\Whisper"
Type: filesandordirs; Name: "{app}\Runtime\EdgeTts"

[Files]
; The bundled application remains self-contained. Replace all executable files
; on upgrade, but let [Code] retain only a plausible existing user settings file.
Source: "{#CompleteStageRoot}\App\*"; DestDir: "{app}"; Excludes: "appsettings.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#CompleteStageRoot}\App\appsettings.json"; Flags: dontcopy

; Python is app-local and never added to PATH or associated with .py files.
Source: "{#CompleteStageRoot}\Runtime\Whisper\*"; DestDir: "{app}\Runtime\Whisper"; Flags: ignoreversion recursesubdirs createallsubdirs

; The staged file set has been audited exactly. This includes all required
; dependent DLLs when the selected FFmpeg build has any.
Source: "{#CompleteStageRoot}\Ffmpeg\bin\*"; DestDir: "{app}\Tools\ffmpeg\bin"; Flags: ignoreversion recursesubdirs createallsubdirs

Source: "{#CompleteStageRoot}\Licenses\*"; DestDir: "{app}\Licenses"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#CompleteStageRoot}\Support\*"; DestDir: "{app}\Support"; Excludes: "VC_redist.x64.exe"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#CompleteStageRoot}\component-manifest.json"; DestDir: "{app}"; DestName: "complete-installer.manifest.json"; Flags: ignoreversion

; These files are embedded in the one Setup EXE and extracted only when Code
; needs them. No external .bin disk slices or payload download is used.
Source: "{#CompleteStageRoot}\Models\whisper\base.pt"; Flags: dontcopy
Source: "{#CompleteStageRoot}\Support\VC_redist.x64.exe"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Check: ShouldCreateProgramsShortcut
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{autostartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: autostart

[Run]
Filename: "{tmp}\VC_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing required Microsoft Visual C++ runtime..."; Flags: runhidden waituntilterminated; Check: ShouldInstallVCRedist
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Model files are intentionally outside {app} and survive uninstall/upgrade.
; These legacy shortcut cleanups match the normal Speak installer.
Type: files; Name: "{commondesktop}\{#MyAppName}.lnk"
Type: files; Name: "{commonprograms}\{#MyAppName}\{#MyAppName}.lnk"
Type: dirifempty; Name: "{commonprograms}\{#MyAppName}"
Type: files; Name: "{app}\uninstall.ps1"
Type: files; Name: "{app}\uninstall.bat"

[Code]
var
  ModelsPage: TInputDirWizardPage;

function HasKnownWhisperLayout(const RootPath: String): Boolean;
var
  WhisperRoot: String;
begin
  WhisperRoot := AddBackslash(RemoveBackslashUnlessRoot(Trim(RootPath))) + 'whisper';
  Result :=
    FileExists(AddBackslash(WhisperRoot) + 'base.pt') or
    FileExists(AddBackslash(WhisperRoot) + 'large-v3.pt') or
    FileExists(AddBackslash(WhisperRoot) + 'large-v3-turbo.pt');
end;

function HasKnownModelLayout(const RootPath: String): Boolean;
var
  NormalizedRoot: String;
begin
  NormalizedRoot := RemoveBackslashUnlessRoot(Trim(RootPath));
  Result :=
    (NormalizedRoot <> '') and
    DirExists(NormalizedRoot) and
    (HasKnownWhisperLayout(NormalizedRoot) or
     FileExists(AddBackslash(NormalizedRoot) +
       'Qwen3-TTS-12Hz-1.7B-CustomVoice\config.json') or
     FileExists(AddBackslash(NormalizedRoot) +
       'Qwen3-TTS-12Hz-1.7B-Base\config.json'));
end;

function ShouldCreateProgramsShortcut(): Boolean;
begin
  Result := not FileExists(ExpandConstant(
    '{commonprograms}\{#MyAppName}\{#MyAppName}.lnk'));
end;

function CompactJson(const Content: AnsiString): AnsiString;
var
  Index: Integer;
  InString: Boolean;
  EscapeNext: Boolean;
begin
  Result := '';
  InString := False;
  EscapeNext := False;
  for Index := 1 to Length(Content) do
  begin
    if InString then
    begin
      Result := Result + Content[Index];
      if EscapeNext then
        EscapeNext := False
      else if Content[Index] = '\' then
        EscapeNext := True
      else if Content[Index] = '"' then
        InString := False;
    end
    else if Content[Index] = '"' then
    begin
      InString := True;
      Result := Result + Content[Index];
    end
    else if (Content[Index] <> ' ') and (Content[Index] <> #9) and
            (Content[Index] <> #10) and (Content[Index] <> #13) then
      Result := Result + Content[Index];
  end;
end;

function JsonStringValueIsEmpty(const CompactContent: AnsiString;
  const Key: String): Boolean;
var
  Needle: AnsiString;
  ValueStart: Integer;
begin
  Needle := '"' + Lowercase(Key) + '":"';
  ValueStart := Pos(Needle, CompactContent);
  if ValueStart = 0 then
  begin
    Result := False;
    exit;
  end;
  ValueStart := ValueStart + Length(Needle);
  Result := (ValueStart <= Length(CompactContent)) and
    (CompactContent[ValueStart] = '"');
end;

function LoadCompactSpeakSettings(const FileName: String;
  var CompactContent: AnsiString): Boolean;
var
  Content: AnsiString;
begin
  Result := False;
  CompactContent := '';
  if not LoadStringFromFile(FileName, Content) then
    exit;
  Content := Trim(Content);
  if (Length(Content) < 2) or (Length(Content) > 1048576) or
     (Content[1] <> '{') or (Content[Length(Content)] <> '}') then
    exit;
  CompactContent := Lowercase(CompactJson(Content));
  Result :=
    (Pos('"transcription"', CompactContent) > 0) and
    (Pos('"whisperpythonpath"', CompactContent) > 0);
end;

function UsesIncompleteLegacyWhisperDefaults(const FileName: String): Boolean;
var
  CompactContent: AnsiString;
  ReleasedV052Defaults: AnsiString;
begin
  Result := False;
  if not LoadCompactSpeakSettings(FileName, CompactContent) then
    exit;

  { Match the released v0.5.2 portable settings after whitespace and case
    normalization. Its wrapper and resident-server paths were deliberately
    non-empty defaults, so they do not prove customization. Exact matching is
    intentionally conservative: changing any path or setting preserves the
    complete existing file instead of replacing unrelated user choices. }
  ReleasedV052Defaults :=
    '{"paths":{' +
      '"toolsroot":"{appdir}",' +
      '"modelsroot":"",' +
      '"workspaceroot":"",' +
      '"cacheroot":"{localappdata}\\speak\\cache"' +
    '},"transcription":{' +
      '"defaultengine":"whisper-local",' +
      '"defaultmodel":"whisper-large-v3",' +
      '"defaultdevice":"cuda",' +
      '"modelkeepaliveminutes":10,' +
      '"whisperpythonpath":"",' +
      '"whisperwrapperpath":"{appdir}\\tools\\speak_worker.py",' +
      '"whispermodelpath":"{modelsroot}\\whisper\\large-v3.pt",' +
      '"whisperserverscriptpath":"{appdir}\\tools\\whisper_resident_server.py"' +
    '},"tts":{' +
      '"defaultengine":"qwen3-customvoice-1.7b",' +
      '"defaultvoice":"aiden",' +
      '"defaultlanguage":"english",' +
      '"outputroot":"",' +
      '"chatterboxpythonpath":"",' +
      '"comfyuipythonpath":"",' +
      '"tortoisepythonpath":"",' +
      '"qwenttscustomvoicemodelpath":"{modelsroot}\\qwen3-tts-12hz-1.7b-customvoice",' +
      '"qwenttsbasemodelpath":"{modelsroot}\\qwen3-tts-12hz-1.7b-base",' +
      '"qwenttsvoicedesignmodelpath":"",' +
      '"tortoisemodeldir":""' +
    '},"cloudstt":{' +
      '"defaultprovider":"groq",' +
      '"defaultendpoint":"https://api.groq.com/openai/v1",' +
      '"defaultmodel":"whisper-large-v3-turbo",' +
      '"defaultapikeyenvvar":"groq_api_key"' +
    '},"llmpolish":{' +
      '"defaultprovider":"off",' +
      '"defaultendpoint":"",' +
      '"defaultmodel":"",' +
      '"defaultapikeyenvvar":"",' +
      '"defaulttimeoutseconds":12' +
    '},"recording":{' +
      '"retentiondays":30' +
    '},"ui":{' +
      '"theme":"dark",' +
      '"keephistory":true,' +
      '"showcompletiontoast":true,' +
      '"autolearncorrections":false,' +
      '"showshortcutwidget":true,' +
      '"minimizetotray":true,' +
      '"startwithwindows":false,' +
      '"dictationshortcut":"ctrl+win"' +
    '},"logging":{' +
      '"level":"information",' +
      '"file":"logs/speak-.log"' +
    '}}';

  Result := JsonStringValueIsEmpty(CompactContent, 'whisperpythonpath') and
    (CompactContent = ReleasedV052Defaults);
end;

function IsLikelyValidSpeakSettings(const FileName: String): Boolean;
var
  CompactContent: AnsiString;
begin
  Result := LoadCompactSpeakSettings(FileName, CompactContent);
end;

function NextSettingsBackupPath(const SettingsPath: String): String;
var
  Attempt: Integer;
begin
  Result := SettingsPath + '.before-complete.bak';
  Attempt := 1;
  while FileExists(Result) do
  begin
    Result := SettingsPath + '.before-complete-' + IntToStr(Attempt) + '.bak';
    Attempt := Attempt + 1;
  end;
end;

function IsExpectedBaseModel(const FileName: String): Boolean;
begin
  Result := FileExists(FileName) and
    (CompareText(GetSHA256OfFile(FileName), '{#BaseModelSha256}') = 0);
end;

procedure InstallCompleteSettings;
var
  SettingsPath: String;
  BackupPath: String;
  TemporarySettingsPath: String;
begin
  SettingsPath := ExpandConstant('{app}\appsettings.json');
  if FileExists(SettingsPath) and IsLikelyValidSpeakSettings(SettingsPath) and
     not UsesIncompleteLegacyWhisperDefaults(SettingsPath) then
    exit;

  ExtractTemporaryFile('appsettings.json');
  TemporarySettingsPath := ExpandConstant('{tmp}\appsettings.json');
  if not IsLikelyValidSpeakSettings(TemporarySettingsPath) then
    RaiseException('The embedded complete offline appsettings.json did not pass validation.');

  BackupPath := '';
  if FileExists(SettingsPath) then
  begin
    if UsesIncompleteLegacyWhisperDefaults(SettingsPath) then
      BackupPath := SettingsPath + '.v052-incomplete.before-complete.bak'
    else
      BackupPath := NextSettingsBackupPath(SettingsPath);
    if FileExists(BackupPath) then
      BackupPath := NextSettingsBackupPath(SettingsPath);
    if not CopyFile(SettingsPath, BackupPath, True) then
      RaiseException('Could not back up the existing Speak appsettings.json.');
  end;

  if not CopyFile(TemporarySettingsPath, SettingsPath, False) then
    RaiseException('Could not install the complete offline appsettings.json.');
  if not IsLikelyValidSpeakSettings(SettingsPath) then
  begin
    if BackupPath <> '' then
      CopyFile(BackupPath, SettingsPath, False);
    RaiseException('The complete offline appsettings.json did not pass validation after installation.');
  end;
end;

procedure InstallBaseModel;
var
  ModelDirectory: String;
  ModelPath: String;
begin
  ModelDirectory := AddBackslash(RemoveBackslashUnlessRoot(Trim(ModelsPage.Values[0]))) + 'whisper';
  ModelPath := AddBackslash(ModelDirectory) + 'base.pt';
  if IsExpectedBaseModel(ModelPath) then
    exit;

  ForceDirectories(ModelDirectory);
  ExtractTemporaryFile('base.pt');
  if not CopyFile(ExpandConstant('{tmp}\base.pt'), ModelPath, False) then
    RaiseException('Could not install the Whisper base model into the selected models folder.');
  if not IsExpectedBaseModel(ModelPath) then
    RaiseException('The installed Whisper base model did not match the audited SHA-256.');
end;

function VersionPart(const Value: String; const PartIndex: Integer): Integer;
var
  Remaining: String;
  Index: Integer;
  Separator: Integer;
  Part: String;
begin
  Remaining := Trim(Value);
  if (Length(Remaining) > 0) and ((Remaining[1] = 'v') or (Remaining[1] = 'V')) then
    Delete(Remaining, 1, 1);

  for Index := 1 to PartIndex - 1 do
  begin
    Separator := Pos('.', Remaining);
    if Separator = 0 then
    begin
      Result := 0;
      exit;
    end;
    Delete(Remaining, 1, Separator);
  end;

  Separator := Pos('.', Remaining);
  if Separator > 0 then
    Part := Copy(Remaining, 1, Separator - 1)
  else
    Part := Remaining;
  Result := StrToIntDef(Part, 0);
end;

function VersionAtLeast(const InstalledVersion: String;
  const RequiredVersion: String): Boolean;
var
  Index: Integer;
  InstalledPart: Integer;
  RequiredPart: Integer;
begin
  for Index := 1 to 4 do
  begin
    InstalledPart := VersionPart(InstalledVersion, Index);
    RequiredPart := VersionPart(RequiredVersion, Index);
    if InstalledPart > RequiredPart then
    begin
      Result := True;
      exit;
    end;
    if InstalledPart < RequiredPart then
    begin
      Result := False;
      exit;
    end;
  end;
  Result := True;
end;

function NeedsVCRedist(): Boolean;
var
  Installed: Cardinal;
  InstalledVersion: String;
begin
  Result := True;
  if RegQueryDWordValue(HKLM64,
       'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
       'Installed', Installed) and
     (Installed = 1) and
     RegQueryStringValue(HKLM64,
       'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
       'Version', InstalledVersion) then
  begin
    Result := not VersionAtLeast(InstalledVersion, '{#VcRedistMinimumVersion}');
  end;
end;

function ShouldInstallVCRedist(): Boolean;
begin
#ifdef TestMode
  Result := False;
#else
  Result := NeedsVCRedist();
#endif
end;

procedure InitializeWizard;
var
  CurrentUserModelsRoot: String;
  LocalMachineModelsRoot: String;
begin
  ModelsPage := CreateInputDirPage(wpSelectDir,
    'Local Whisper model',
    'Choose where Speak should install the included Whisper base model.',
    'Speak installs base.pt in a whisper subfolder. Existing valid model roots and settings are preserved during upgrade.',
    False, '');
  ModelsPage.Add('Models folder:');

  CurrentUserModelsRoot := '';
  LocalMachineModelsRoot := '';
  RegQueryStringValue(HKCU, '{#SpeakRegistrySubkey}', 'ModelsRoot', CurrentUserModelsRoot);
  RegQueryStringValue(HKLM64, '{#SpeakRegistrySubkey}', 'ModelsRoot', LocalMachineModelsRoot);

  if Trim(ExpandConstant('{param:ModelsRoot|}')) <> '' then
    ModelsPage.Values[0] := ExpandConstant('{param:ModelsRoot|}')
  else if HasKnownModelLayout(CurrentUserModelsRoot) then
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
    MsgBox('Choose a folder for the included Whisper base model.', mbError, MB_OK);
    Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    InstallCompleteSettings;
    InstallBaseModel;
    ExtractTemporaryFile('VC_redist.x64.exe');

#ifndef TestMode
    RegWriteStringValue(HKLM64, '{#SpeakRegistrySubkey}', 'ModelsRoot', ModelsPage.Values[0]);
#endif
    RegWriteStringValue(HKCU, '{#SpeakRegistrySubkey}', 'ModelsRoot', ModelsPage.Values[0]);
  end;
end;

#ifdef TestMode
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKCU, '{#SpeakRegistrySubkey}', 'ModelsRoot');
    RegDeleteKeyIfEmpty(HKCU, '{#SpeakRegistrySubkey}');
  end;
end;
#endif
