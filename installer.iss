[Setup]
AppId=BmtTemperatureExporter
AppName=BMT Temperature Exporter
AppVersion=0.1.0
DefaultDirName={localappdata}\Programs\BMT Temperature Exporter
DefaultGroupName=BMT Temperature Exporter
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
SetupIconFile=app.ico
UninstallDisplayIcon={app}\BmtExporter.exe
OutputDir=dist
OutputBaseFilename=BmtExporter-0.1.0-Setup-x64
Compression=lzma2
SolidCompression=yes
DisableProgramGroupPage=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "dist\BmtExporter-0.1.0-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\BMT Temperature Exporter"; Filename: "{app}\BmtExporter.exe"
Name: "{group}\BMT Temperature Exporter Manual"; Filename: "{app}\USER-GUIDE.txt"
Name: "{userdesktop}\BMT Temperature Exporter"; Filename: "{app}\BmtExporter.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\BmtExporter.exe"; Description: "Launch BMT Temperature Exporter"; Flags: nowait postinstall skipifsilent
