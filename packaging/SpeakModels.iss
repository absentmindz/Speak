; Optional offline model pack for Speak.

#define MyPackName "Speak Offline Models"
#define MyPackVersion "0.5.0"

[Setup]
AppId={{2A576999-3E04-47AF-9D4A-7FD07C03D2A5}
AppName={#MyPackName}
AppVersion={#MyPackVersion}
DefaultDirName={commonappdata}\Speak\Models
DisableProgramGroupPage=yes
OutputDir=output\models
OutputBaseFilename=Speak-{#MyPackVersion}-Offline-Models-Setup
Compression=none
SolidCompression=no
DiskSpanning=yes
DiskSliceSize=2100000000
SlicesPerDisk=1
WizardStyle=modern dark
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\app.ico
Uninstallable=no
CreateAppDir=yes
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "D:\Models\whisper\large-v3.pt"; DestDir: "{app}\whisper"; Flags: ignoreversion
Source: "D:\Models\Qwen3-TTS-12Hz-1.7B-CustomVoice\*"; DestDir: "{app}\Qwen3-TTS-12Hz-1.7B-CustomVoice"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "D:\Models\Qwen3-TTS-12Hz-1.7B-Base\*"; DestDir: "{app}\Qwen3-TTS-12Hz-1.7B-Base"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "stage\ModelLicenses\*"; DestDir: "{app}\Licenses"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "stage\models-manifest.sha256"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
Root: HKLM64; Subkey: "SOFTWARE\Speak"; ValueType: string; ValueName: "ModelsRoot"; ValueData: "{app}"; Flags: preservestringtype

[Code]
procedure InitializeWizard;
var
  ExistingModelsRoot: String;
begin
  if RegQueryStringValue(HKLM64, 'SOFTWARE\Speak', 'ModelsRoot', ExistingModelsRoot) then
    WizardForm.DirEdit.Text := ExistingModelsRoot;
end;
