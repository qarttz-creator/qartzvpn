; Inno Setup script для Qarttz VPN.
; Собирается в GitHub Actions: ISCC.exe /DMyAppVersion=1.0.0 installer\QarttzVPN.iss
; Ядра xray.exe и sing-box.exe кладутся в ..\publish\core ДО компиляции (это делает CI).

#define MyAppName "Qarttz VPN"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif
#ifndef SrcDir
  #define SrcDir "..\publish"
#endif

[Setup]
; AppId фиксирован — чтобы обновления заменяли прошлую версию, а не плодили копии.
AppId={{8F2C9A41-3B7D-4E16-9C5A-1D2E3F4A5B6C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Qarttz
DefaultDirName={autopf}\QarttzVPN
DefaultGroupName=Qarttz VPN
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=QarttzVPN-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Клиенту для TUN нужны права администратора → ставим в Program Files от админа.
PrivilegesRequired=admin
; Мьютекс приложения — установщик сам предложит закрыть запущенный клиент при обновлении.
AppMutex=QarttzVPN_SingleInstance
WizardStyle=modern
UninstallDisplayName=Qarttz VPN

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Всё из publish, кроме символов и пользовательских файлов (их кладём отдельно).
Source: "{#SrcDir}\*"; DestDir: "{app}"; \
  Excludes: "*.pdb,ПОЛОЖИТЬ_СЮДА_ядра.txt,exclude.txt,profiles.txt,apps.txt"; \
  Flags: ignoreversion recursesubdirs createallsubdirs
; эти три файла пользователь правит из клиента, при обновлении НЕ затираем.
Source: "{#SrcDir}\profiles.txt"; DestDir: "{app}"; Flags: onlyifdoesntexist
Source: "{#SrcDir}\exclude.txt"; DestDir: "{app}"; Flags: onlyifdoesntexist
Source: "{#SrcDir}\apps.txt"; DestDir: "{app}"; Flags: onlyifdoesntexist

[Icons]
Name: "{group}\Qarttz VPN"; Filename: "{app}\QarttzVPN.exe"
Name: "{group}\{cm:UninstallProgram,Qarttz VPN}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Qarttz VPN"; Filename: "{app}\QarttzVPN.exe"; Tasks: desktopicon

[Run]
; shellexec — иначе запуск admin-приложения из админского установщика падает
; с "требуется повышение" (CreateProcess не умеет авто-elevation).
Filename: "{app}\QarttzVPN.exe"; Description: "{cm:LaunchProgram,Qarttz VPN}"; \
  Flags: nowait postinstall skipifsilent shellexec

[UninstallRun]
; На всякий случай гасим ядра перед удалением, чтобы файлы не были заняты.
Filename: "{sys}\taskkill.exe"; Parameters: "/f /t /im xray.exe"; Flags: runhidden; RunOnceId: "killxray"
Filename: "{sys}\taskkill.exe"; Parameters: "/f /t /im sing-box.exe"; Flags: runhidden; RunOnceId: "killsb"
