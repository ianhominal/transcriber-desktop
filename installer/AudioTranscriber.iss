; Inno Setup script para Audio Transcriber
; Compilar con: ISCC.exe AudioTranscriber.iss
; (o desde el IDE de Inno Setup: Build > Compile)

#define MyAppName "Audio Transcriber"
#define MyAppVersion "1.0.0"
#define MyAppExeName "AudioTranscriber.exe"

[Setup]
AppId={{7F3A2C1E-9B4D-4E6A-8C2F-1A2B3C4D5E6F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Ian
DefaultDirName={autopf}\AudioTranscriber
DefaultGroupName=Audio Transcriber
DisableProgramGroupPage=yes
OutputDir=..\publish\installer
OutputBaseFilename=AudioTranscriber-Setup-{#MyAppVersion}
SetupIconFile=..\src\AudioTranscriber.App\appicon.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; GroupDescription: "Accesos directos:"

[Files]
; Self-contained + single-file: el .exe embebe el runtime .NET, pero además hay
; nativas sueltas (D3DCompiler, PresentationNative, e_sqlite3, etc.) y una carpeta
; runtimes\ que dotnet publish copia igual. Se empaqueta toda la carpeta de salida
; con wildcard para no tener que listar archivos a mano ni romper el build si cambia
; el layout (self-contained single-file vs framework-dependent).
Source: "..\publish\portable\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirmirrors; Excludes: "*.pdb"

[Icons]
Name: "{group}\Audio Transcriber"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Desinstalar Audio Transcriber"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Audio Transcriber"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Abrir Audio Transcriber"; Flags: nowait postinstall skipifsilent
