[Setup]
AppId=BmtThermalAnalysisStudio
AppName=BMT Thermal Analysis Studio
AppVersion=0.4.0
DefaultDirName={localappdata}\Programs\BMT Thermal Analysis Studio
DefaultGroupName=BMT Thermal Analysis Studio
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
SetupIconFile=app.ico
UninstallDisplayIcon={app}\BmtExporter.exe
OutputDir=dist
OutputBaseFilename=BmtStudio-0.4.0-Setup-x64
Compression=lzma2
SolidCompression=yes
DisableProgramGroupPage=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "dist\BmtStudio-0.4.0-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\BMT Thermal Analysis Studio"; Filename: "{app}\BmtExporter.exe"
Name: "{group}\BMT Temperature Exporter Manual"; Filename: "{app}\USER-GUIDE.txt"
Name: "{userdesktop}\BMT Thermal Analysis Studio"; Filename: "{app}\BmtExporter.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\BmtExporter.exe"; Description: "Launch BMT Thermal Analysis Studio"; Flags: nowait postinstall skipifsilent
