; Установщик PC Toolkit (Inno Setup 7).
; Собирается командой installer.cmd в корне проекта: она передаёт версию и папку с собранным приложением.
;   iscc /DAppVersion=0.1.4 /DAppFiles=Z:\My_projekt\publish\PcToolkit-0.1.4-win-x64 installer\PcToolkit.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AppFiles
  #error Не задана папка с собранным приложением: /DAppFiles=...
#endif

; Имена — как в ServiceProtocol.ServiceName, SingleInstance.MutexName и SingleInstance.ExitEventName.
#define ServiceName "PcToolkitService"
#define AppMutexName "Local\PcToolkit.SingleInstance"
#define ExitEventName "Local\PcToolkit.Exit"

[Setup]
AppId={{8C5D1F7E-3A4B-4E2C-9C1D-7B2E5F6A9D30}
AppName=PC Toolkit
AppVersion={#AppVersion}
AppVerName=PC Toolkit {#AppVersion}
AppPublisher=PC Toolkit
VersionInfoVersion={#AppVersion}
VersionInfoProductName=PC Toolkit
VersionInfoDescription=PC Toolkit Setup
; Папка выбирается на отдельной странице; при обновлении предлагается прежняя.
DefaultDirName={autopf}\PC Toolkit
DisableDirPage=no
UsePreviousAppDir=yes
; Вместо страницы «папка в меню Пуск» — галочки на странице задач.
DisableProgramGroupPage=yes
UsePreviousTasks=yes
DisableWelcomePage=no
OutputBaseFilename=PcToolkit-{#AppVersion}-Setup
SetupIconFile=..\src\PcToolkit.Desktop\Assets\PcToolkit.ico
UninstallDisplayIcon={app}\PcToolkit.exe
UninstallDisplayName=PC Toolkit
WizardStyle=modern dark
; Картинки в цветах приложения (installer\wizard, рисует installer\make-wizard-images.py).
; Каждая в нескольких размерах: мастер берёт подходящий под масштаб экрана.
WizardImageFile=wizard\WizardImage-*.png
WizardSmallImageFile=wizard\WizardSmall-*.png
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1607 и новее — как требует .NET 10.
MinVersion=10.0.14393
; Program Files и служба Windows — только с правами администратора.
; Ключ /CURRENTUSER ставит без прав администратора в папку пользователя (без службы) — для проверки мастера.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
; Запущенный PC Toolkit (в том числе свёрнутый в трей) закрывается перед заменой файлов — см. CloseApp в [Code].
; AppMutex не задан: он только просит закрыть программу вручную и срабатывает раньше, чем CloseApp.
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=no
LanguageDetectionMethod=uilanguage

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.GroupShortcuts=Ярлыки:
russian.TaskDesktop=Создать ярлык на рабочем столе
russian.TaskStartMenu=Создать ярлык в меню «Пуск»
russian.GroupSensors=Датчики:
russian.TaskService=Установить службу PC Toolkit: температуры и все датчики без запуска от имени администратора
russian.StatusService=Установка службы PC Toolkit…
russian.StatusServiceUpdate=Обновление службы PC Toolkit…
russian.RunApp=Запустить PC Toolkit
russian.RemoveData=Удалить также настройки, журналы и замеры FPS PC Toolkit?%n%nЗаписи сессий мониторинга в папке «Документы\PC Toolkit» останутся.
russian.AppRunning=PC Toolkit сейчас запущен.%n%nЗакройте его: щёлкните правой кнопкой по значку PC Toolkit в трее (возле часов) и выберите «Выход». Затем нажмите «ОК».
russian.BadDir=В этой папке живёт служба PC Toolkit, приложение туда ставить нельзя.%n%nВыберите другую папку, например:%n%1
english.GroupShortcuts=Shortcuts:
english.TaskDesktop=Create a desktop shortcut
english.TaskStartMenu=Create a Start menu shortcut
english.GroupSensors=Sensors:
english.TaskService=Install the PC Toolkit service: all sensors without running as administrator
english.StatusService=Installing the PC Toolkit service...
english.StatusServiceUpdate=Updating the PC Toolkit service...
english.RunApp=Launch PC Toolkit
english.RemoveData=Also remove PC Toolkit settings, logs and FPS recordings?%n%nMonitoring sessions in "Documents\PC Toolkit" are kept.
english.AppRunning=PC Toolkit is running.%n%nPlease close it: right-click the PC Toolkit icon in the notification area and choose "Выход" (Exit). Then click OK.
english.BadDir=This folder belongs to the PC Toolkit service.%n%nPlease choose another folder, for example:%n%1

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktop}"; GroupDescription: "{cm:GroupShortcuts}"
Name: "startmenuicon"; Description: "{cm:TaskStartMenu}"; GroupDescription: "{cm:GroupShortcuts}"
; Если служба уже стоит, она обновляется сама, без вопроса.
Name: "service"; Description: "{cm:TaskService}"; GroupDescription: "{cm:GroupSensors}"; Check: IsAdminInstallMode and not ServiceInstalled

[Files]
Source: "{#AppFiles}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\PC Toolkit"; Filename: "{app}\PcToolkit.exe"; Tasks: startmenuicon
Name: "{autodesktop}\PC Toolkit"; Filename: "{app}\PcToolkit.exe"; Tasks: desktopicon

[Run]
; Служба копируется в Program Files\PcToolkit\Service самим приложением (как кнопкой «Обновить службу» в настройках).
Filename: "{app}\PcToolkit.exe"; Parameters: "--install-service"; StatusMsg: "{cm:StatusServiceUpdate}"; Flags: runhidden waituntilterminated; Check: IsAdminInstallMode and ServiceInstalled
Filename: "{app}\PcToolkit.exe"; Parameters: "--install-service"; StatusMsg: "{cm:StatusService}"; Flags: runhidden waituntilterminated; Tasks: service
; Приложение запускается с обычными правами, а не с правами установщика.
Filename: "{app}\PcToolkit.exe"; Description: "{cm:RunApp}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Сначала остановить и удалить службу: её файлы лежат отдельно, в Program Files\PcToolkit\Service.
Filename: "{app}\PcToolkit.exe"; Parameters: "--uninstall-service"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"; Check: IsAdminInstallMode

[UninstallDelete]
Type: dirifempty; Name: "{commonpf64}\PcToolkit"

[Registry]
; Запись автозапуска делает само приложение (Настройки → запуск с Windows); при удалении её надо убрать.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "PC Toolkit"; ValueType: none; Flags: uninsdeletevalue

[Code]
const
  EVENT_MODIFY_STATE = $0002;

function WinOpenEvent(DesiredAccess: Cardinal; InheritHandle: Cardinal; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function WinSetEvent(Event: THandle): Integer;
  external 'SetEvent@kernel32.dll stdcall';
function WinCloseHandle(Handle: THandle): Integer;
  external 'CloseHandle@kernel32.dll stdcall';

function AppRunning: Boolean;
begin
  Result := CheckForMutexes('{#AppMutexName}');
end;

{ Просит запущенный PC Toolkit закрыться (как «Выход» в меню значка в трее) и ждёт до 15 секунд.
  Так умеют версии начиная с 0.1.5; у более старых сигнала нет. }
procedure AskAppToExit;
var
  Event: THandle;
  I: Integer;
begin
  Event := WinOpenEvent(EVENT_MODIFY_STATE, 0, '{#ExitEventName}');
  if Event = 0 then
    Exit;
  WinSetEvent(Event);
  WinCloseHandle(Event);
  for I := 1 to 75 do
  begin
    if not AppRunning then
      Exit;
    Sleep(200);
  end;
end;

{ Пока PC Toolkit работает, его файлы заняты: закрываем его сами, а если не вышло — просим закрыть вручную. }
function CloseApp: Boolean;
begin
  Result := True;
  while AppRunning do
  begin
    AskAppToExit;
    if not AppRunning then
      Break;
    if SuppressibleMsgBox(CustomMessage('AppRunning'), mbError, MB_OKCANCEL, IDCANCEL) <> IDOK then
    begin
      Result := False;
      Break;
    end;
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := CloseApp;
end;

function InitializeUninstall: Boolean;
begin
  Result := CloseApp;
end;

function ServiceInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\{#ServiceName}');
end;

{ Папка службы: Program Files\PcToolkit\Service. Приложение в неё (или над ней) ставить нельзя:
  при обновлении служба копируется из папки приложения туда же. }
function IsServiceFolder(Dir: String): Boolean;
var
  ServiceRoot: String;
begin
  ServiceRoot := ExpandConstant('{commonpf64}\PcToolkit');
  Dir := RemoveBackslashUnlessRoot(Dir);
  Result := (CompareText(Dir, ServiceRoot) = 0)
    or (CompareText(Copy(Dir, 1, Length(ServiceRoot) + 1), ServiceRoot + '\') = 0);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpSelectDir) and IsServiceFolder(WizardDirValue) then
  begin
    MsgBox(FmtMessage(CustomMessage('BadDir'), [ExpandConstant('{commonpf64}\PC Toolkit')]), mbError, MB_OK);
    Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
  begin
    if MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DelTree(ExpandConstant('{localappdata}\PcToolkit'), True, True, True);
      DelTree(ExpandConstant('{commonappdata}\PcToolkit'), True, True, True);
    end;
  end;
end;
