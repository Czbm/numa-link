; Inno Setup Script — Numa Link (con cierre de Systray, desinstalación previa, WinFsp y Firewall)
; Compilar con Inno Setup 6: iscc installer\NumaLink.iss

[Setup]
AppId={{8F6E2A14-4C9B-4E91-9A2D-7B41E5C01F9A}
AppName=Numa Link
AppVersion=1.0.7
AppPublisher=Numa
DefaultDirName={autopf}\Numa Link
DefaultGroupName=Numa Link
OutputDir=..\publish-installer
OutputBaseFilename=NumaLink-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
CloseApplications=force
CloseApplicationsFilter=NumaLink.exe
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Crear acceso directo en el Escritorio"; GroupDescription: "Accesos directos:"
Name: "startup"; Description: "Iniciar Numa Link minimizado al arrancar Windows"; GroupDescription: "Segundo plano:"

[Files]
Source: "..\publish\NumaLink.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "winfsp-2.2.26323.msi"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\Numa Link"; Filename: "{app}\NumaLink.exe"
Name: "{autodesktop}\Numa Link"; Filename: "{app}\NumaLink.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "NumaLink"; ValueData: """{app}\NumaLink.exe"" --minimized"; Flags: uninsdeletevalue; Tasks: startup

[Run]
; 1. Instalar WinFsp en silencio solo si no existe winfsp-x64.dll
Filename: "msiexec.exe"; Parameters: "/i ""{tmp}\winfsp-2.2.26323.msi"" /qn /norestart"; StatusMsg: "Instalando componente de unidad virtual WinFsp..."; Check: NeedsWinFsp
; 2. Reglas de Firewall de Windows en red privada (UDP 45821 descubrimiento + TCP 45822 TLS)
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Numa Link (LAN Discovery UDP)"""; Flags: runhidden
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Numa Link (LAN Transfer TCP)"""; Flags: runhidden
Filename: "netsh.exe"; Parameters: "advfirewall firewall add rule name=""Numa Link (LAN Discovery UDP)"" dir=in action=allow protocol=UDP localport=45821 profile=private"; Flags: runhidden
Filename: "netsh.exe"; Parameters: "advfirewall firewall add rule name=""Numa Link (LAN Transfer TCP)"" dir=in action=allow protocol=TCP localport=45822 profile=private"; Flags: runhidden
; 3. Iniciar Numa Link al finalizar
Filename: "{app}\NumaLink.exe"; Description: "Ejecutar Numa Link ahora"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/F /IM NumaLink.exe /T"; Flags: runhidden
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Numa Link (LAN Discovery UDP)"""; Flags: runhidden
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Numa Link (LAN Transfer TCP)"""; Flags: runhidden

[Code]
procedure StopRunningNumaLink();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM NumaLink.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(400);
end;

function GetPreviousUninstallString(): String;
var
  KeyPath: String;
  UninstStr: String;
begin
  KeyPath := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F6E2A14-4C9B-4E91-9A2D-7B41E5C01F9A}_is1';
  UninstStr := '';
  if not RegQueryStringValue(HKLM, KeyPath, 'UninstallString', UninstStr) then
    RegQueryStringValue(HKCU, KeyPath, 'UninstallString', UninstStr);
  Result := UninstStr;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  UninstStr: String;
  ResultCode: Integer;
begin
  Result := '';
  // 1. Cerrar cualquier instancia activa (incluyendo las minimizadas en System Tray)
  StopRunningNumaLink();

  // 2. Desinstalar silenciosamente cualquier versión anterior registrada
  UninstStr := GetPreviousUninstallString();
  if UninstStr <> '' then
  begin
    UninstStr := RemoveQuotes(UninstStr);
    Exec(UninstStr, '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(500);
  end;
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningNumaLink();
  Result := True;
end;

function NeedsWinFsp(): Boolean;
begin
  Result := not FileExists(ExpandConstant('{pf32}\WinFsp\bin\winfsp-x64.dll'))
        and not FileExists(ExpandConstant('{pf64}\WinFsp\bin\winfsp-x64.dll'));
end;
