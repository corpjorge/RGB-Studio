#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif

[Setup]
AppId={{3925A6DB-9B80-4790-9CD7-13C7DFD01FD2}
AppName=RGB Studio
AppVersion={#AppVersion}
AppVerName=RGB Studio {#AppVersion}
AppPublisher=RGB Studio
AppPublisherURL=https://github.com/corpjorge/RGB-Studio
AppSupportURL=https://github.com/corpjorge/RGB-Studio/issues
AppUpdatesURL=https://github.com/corpjorge/RGB-Studio/releases/latest
DefaultDirName={localappdata}\Programs\RgbStudio
DefaultGroupName=RGB Studio
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir={#OutputDir}
OutputBaseFilename=RGB-Studio-{#AppVersion}-win-x64-Setup
SetupIconFile=..\App\Assets\RgbStudio.ico
UninstallDisplayIcon={app}\RgbStudio.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
AppMutex=Local\RgbStudio.SingleInstance
CloseApplications=yes
CloseApplicationsFilter=RgbStudio.exe,*.dll
RestartApplications=no
UninstallDisplayName=RGB Studio
VersionInfoVersion={#AppVersion}.0
InfoBeforeFile=Requisitos.txt

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; GroupDescription: "Accesos directos:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Update-RgbStudio.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "rgbstudio-installed.marker"; DestDir: "{app}"; Flags: ignoreversion
Source: "Requisitos.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "ThirdPartyNotices.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\RGB Studio"; Filename: "{app}\RgbStudio.exe"; WorkingDir: "{app}"
Name: "{group}\Desinstalar RGB Studio"; Filename: "{uninstallexe}"
Name: "{autodesktop}\RGB Studio"; Filename: "{app}\RgbStudio.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\RgbStudio.exe"; Description: "Iniciar RGB Studio"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
