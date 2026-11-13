; ============================================================================
; installer.iss — скрипт сборки установщика для Inno Setup 6.
;
; Что делает:
;   1. упаковывает содержимое publish\ (exe службы + appsettings.json)
;      в один файл NAPS2.WebScan.LocalService.Setup.exe;
;   2. при запуске Setup.exe создаёт каталог службы
;      "C:\Program Files\NAPS2 WebScan Local Service" и копирует туда файлы;
;   3. останавливает/удаляет старую версию службы, если она уже была;
;   4. регистрирует службу "NAPS2.WebScan Service" с отложенным автозапуском
;      и зависимостями от WIA (StiSvc) и RPC (RpcSs), включает перезапуск
;      при сбоях и запускает службу;
;   5. при удалении программы останавливает и удаляет службу.
;
; Сборка установщика (из корня репозитория, после dotnet publish):
;   & "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" .\installer.iss
; Результат: installer\NAPS2.WebScan.LocalService.Setup.exe
; ============================================================================

[Setup]
AppName=NAPS2 WebScan Local Service
AppVersion=1.0
; Каталог установки создаётся автоматически: {autopf} = Program Files
DefaultDirName={autopf}\NAPS2 WebScan Local Service
DefaultGroupName=NAPS2 WebScan
; Куда положить готовый Setup.exe
OutputDir=.\installer
OutputBaseFilename=NAPS2.WebScan.LocalService.Setup
Compression=lzma2
SolidCompression=yes
; Установка требует прав администратора: служба ставится в Program Files
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

[Files]
; Копируем всё, что собрал dotnet publish, в каталог службы {app}
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Run]
; 0a. Останавливаем службу, если она уже установлена
Filename: "{sys}\sc.exe"; Parameters: "stop ""NAPS2.WebScan Service"""; Flags: runhidden; Check: ServiceExists

; 0b. Удаляем старую службу, если она есть
Filename: "{sys}\sc.exe"; Parameters: "delete ""NAPS2.WebScan Service"""; Flags: runhidden; Check: ServiceExists

; 1. Создаём службу с отложенным запуском и зависимостями от WIA (StiSvc) и RPC (RpcSs)
Filename: "{sys}\sc.exe"; Parameters: "create ""NAPS2.WebScan Service"" binPath= ""{app}\NAPS2.WebScan.LocalService.exe"" start= delayed-auto depend= StiSvc/RpcSs"; Flags: runhidden

; 2. Настраиваем автоматический перезапуск при сбоях (три попытки с интервалом 60 секунд)
Filename: "{sys}\sc.exe"; Parameters: "failure ""NAPS2.WebScan Service"" reset= 0 actions= restart/60000/restart/60000/restart/60000"; Flags: runhidden

; 3. Запускаем службу после установки
Filename: "{sys}\sc.exe"; Parameters: "start ""NAPS2.WebScan Service"""; Flags: runhidden

[UninstallRun]
; Останавливаем и удаляем службу при деинсталляции
Filename: "{sys}\sc.exe"; Parameters: "stop ""NAPS2.WebScan Service"""; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "delete ""NAPS2.WebScan Service"""; Flags: runhidden

[Code]
// Проверка «служба уже существует»: нужна, чтобы stop/delete выполнялись
// только при обновлении, а не при первой установке
function ServiceExists(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('cmd.exe', '/c sc query "NAPS2.WebScan Service" > nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;
