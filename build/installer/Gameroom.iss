; Gameroom installer, compiled by build/publish.ps1 with Inno Setup 6.
;   /DAppVersion=1.0.0         version (from GameroomVersion in source/Directory.Build.props)
;   /DSourceDir=<dir>          Release build output
;   /DOutputDir=<dir>          where the installer is written
;   /DSpanned                  split into setup exe + .bin parts below the 50 MB file limit of the update server

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\Release"
#endif
#ifndef OutputDir
  #define OutputDir "Output"
#endif

[Setup]
; Must match ReleaseChannel.InstallerAppId (source/Playnite/Gameroom/Updates/GameroomReleases.cs).
AppId={{3BDEDB77-2B61-494C-AC81-AA56250648D3}
AppName=Gameroom
AppVersion={#AppVersion}
AppVerName=Gameroom {#AppVersion}
AppPublisher=Gameroom
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Gameroom
; Per user installation into %LOCALAPPDATA%\Programs, so updates never need administrator rights.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\Gameroom
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=GameroomSetup-{#AppVersion}
SetupIconFile=..\..\source\Playnite.DesktopApp\Themes\Desktop\Default\Images\applogo.ico
UninstallDisplayIcon={app}\Gameroom.exe
UninstallDisplayName=Gameroom
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
; Gameroom must not run while its files are replaced.
AppMutex=GameroomInstanceMutex
CloseApplications=yes
RestartApplications=no
#ifdef Spanned
DiskSpanning=yes
; 45 MB parts.
DiskSliceSize=47185920
#endif

[Languages]
; The first language is used when Windows uses none of them.
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
spanish.UpdaterShortcut=Buscar actualizaciones de Gameroom
english.UpdaterShortcut=Check for Gameroom updates
spanish.FullscreenShortcut=Gameroom (pantalla completa)
english.FullscreenShortcut=Gameroom (fullscreen)
spanish.DeleteUserData=¿Eliminar también tu biblioteca, tiempos de juego y copias de partidas guardadas de Gameroom?%n%nSi eliges No, se conservarán para cuando vuelvas a instalar Gameroom.
english.DeleteUserData=Also delete your Gameroom library, playtime and save backups?%n%nIf you choose No, they are kept for when you install Gameroom again.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Development config and files Playnite's own build doesn't ship either.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; \
    Excludes: "*.pdb,*.xml,Common.config,CefSharp.BrowserSubprocess.exe,System.Management.Automation.dll,Windows.winmd,config.json,*.log,\library\*,\SaveBackups\*,\Backup\*,\browsercache\*,\cache\*,\ExtensionsData\*,\JITProfiles\*"
Source: "Common.release.config"; DestDir: "{app}"; DestName: "Common.config"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Gameroom"; Filename: "{app}\Gameroom.exe"
Name: "{autoprograms}\{cm:FullscreenShortcut}"; Filename: "{app}\Gameroom.Fullscreen.exe"
Name: "{autoprograms}\{cm:UpdaterShortcut}"; Filename: "{app}\GameroomUpdater.exe"
Name: "{autodesktop}\Gameroom"; Filename: "{app}\Gameroom.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Gameroom.exe"; Description: "{cm:LaunchProgram,Gameroom}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  DataDir := ExpandConstant('{userappdata}\Gameroom');
  if (CurUninstallStep = usPostUninstall) and DirExists(DataDir) and not UninstallSilent then
  begin
    if MsgBox(CustomMessage('DeleteUserData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(DataDir, True, True, True);
  end;
end;
