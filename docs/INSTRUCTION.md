# Сборка, установка и эксплуатация NAPS2.WebScan

Инструкция как собрать, установить и проверить службу.
Описание службы, её устройство и ограничения в [README](../README.md).

## Краткий путь

```text
1. Установить .NET 8 SDK (если его нет)
2. dotnet publish  →  папка publish\
3. Установить Inno Setup (если его нет)
4. Открыть installer.iss в Inno Setup → Build → Compile
   →  installer\NAPS2.WebScan.LocalService.Setup.exe
5. Запустить Setup.exe от администратора  →  служба установлена и работает
```

---

## 1. Требования

### 1.1. Компьютер, на который ставится служба

- Windows 10/11 **x64**
- Права **администратора** (установка идёт в `Program Files`)
- Сканер, видимый в системе:
  - для USB - **WIA-совместимый драйвер**
  - для сетевого МФУ - поддержка **eSCL/AirScan** и доступ по HTTP
- Свободные порты `9880+` и `9899`

Проверить сканеры и службу WIA:

```powershell
Get-Service StiSvc            # должно быть Running
Get-PnpDevice -Class Image    # список сканеров/камер
```

### 1.2. Компьютер, на котором собирается служба

- **.NET 8 SDK** компилирует исходники в `exe`. Нужен именно **SDK**, а не
  Runtime: Runtime только запускает готовые программы, SDK умеет собирать
- **Inno Setup** упаковывает собранные файлы в установщик `Setup.exe`

---

## 2. Установка .NET 8 SDK

Если `dotnet --version` уже отвечает `8.0.x`, раздел можно пропустить.

1. Откройте <https://dotnet.microsoft.com/download/dotnet/8.0>
2. Найдите **SDK 8.0** (именно **SDK**, а не Runtime) для **Windows x64**
3. Скачайте и запустите установщик, нажимайте «Далее» / «Install»
4. **Закройте и заново откройте** PowerShell, иначе он не увидит новую команду
5. Проверьте:

```powershell
dotnet --version
```

Должно появиться `8.0.4xx`.

---

## 3. Сборка службы

Если команда `dotnet` не найдена («не является внутренней или внешней
командой»), значит **.NET 8 SDK не установлен**. Скачайте его и установите
по разделу 2, затем перезапустите PowerShell.

Из **корня** репозитория (там, где лежит `installer.iss`) пропишите:

```powershell
dotnet publish .\NAPS2.WebScan.LocalService\NAPS2.WebScan.LocalService.csproj `
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish
```

Что произойдёт:

- служба скомпилируется;
- в папке `publish\` появятся:

| Файл                             | Размер | Назначение      |
| -------------------------------- | ------ | --------------- |
| `NAPS2.WebScan.LocalService.exe` | ~76 МБ | служба          |
| `appsettings.json`               | ~2 КБ  | файл настроек   |
| `NAPS2.WebScan.LocalService.pdb` | ~23 КБ | символы отладки |

---

## 4. Сборка установщика (Inno Setup)

### 4.1. Установить Inno Setup

Если Inno Setup ещё не установлен, скачайте его с
<https://jrsoftware.org/isinfo.php> и установите как обычную программу
(подойдёт версия 6 или 7). Путь к компилятору нужен только для способа
формирования установщика из PowerShell:

- Inno Setup 6 - `C:\Program Files (x86)\Inno Setup 6\ISCC.exe`;
- Inno Setup 7 - `C:\Program Files\Inno Setup 7\ISCC.exe`.

### 4.2. Собрать `Setup.exe`

Перед сборкой убедитесь, что в `publish\` лежит новый `exe`: установщик
копирует **всё** содержимое `publish\`.

**Основной способ через интерфейс Inno Setup:**

1. Откройте файл `installer.iss` из корня репозитория (**File → Open**)
2. Запустите компиляцию: меню **Build → Compile**
3. Дождитесь окончания: внизу будет `Successful compile`
4. Готовый файл `installer\NAPS2.WebScan.LocalService.Setup.exe`

**Дополнительный способ через PowerShell**

Команда из корня репозитория (пример для Inno Setup 6):

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" .\installer.iss
```

Результат тот же:

```text
installer\NAPS2.WebScan.LocalService.Setup.exe
```

### 4.3. Что делает установщик

`installer.iss` описывает два этапа.

**При сборке `Setup.exe`:**

- берёт всё из `publish\`;
- сжимает в один `NAPS2.WebScan.LocalService.Setup.exe`.

**При запуске `Setup.exe` на компьютере:**

1. просит права администратора;
2. **создаёт каталог** `C:\Program Files\NAPS2 WebScan Local Service`
   и копирует туда `exe`, `appsettings.json` и остальные файлы из `publish\`;
3. если служба уже установлена, то останавливает её и удаляет старую версию;
4. регистрирует службу `NAPS2.WebScan Service`:
   - `start= delayed-auto`: отложенный автозапуск;
   - `depend= StiSvc/RpcSs`: зависимости от службы образов Windows (WIA) и
     службы RPC;
5. настраивает перезапуск при сбое: 3 попытки по 60 секунд;
6. запускает службу;
7. при удалении программы останавливает и удаляет службу.

---

## 5. Установка службы

1. Скопируйте `NAPS2.WebScan.LocalService.Setup.exe` на целевой компьютер
2. Запустите его **от имени администратора**
3. Пройдите установку, нажимая «Далее»
4. В конце установщик сам запустит службу

Если Windows SmartScreen предупреждает о неизвестном издателе - это ожидаемо
для неподписанного `exe`. Нажмите «Подробнее» → «Выполнить в любом случае».
Чтобы предупреждения не было, `exe` подписывают сертификатом.

---

## 6. Проверка после установки

### 6.1. Состояние службы

```powershell
sc.exe query "NAPS2.WebScan Service"
```

Ожидается `STATE : 4 RUNNING`.

### 6.2. Список сканеров

```powershell
Invoke-RestMethod http://localhost:9899/devices | ConvertTo-Json -Depth 5
Test-NetConnection 127.0.0.1 -Port 9899
```

Пустой список, если сканеры не найдены:

```json
{ "devices": [] }
```

Или через браузер: <http://localhost:9899/devices>

### 6.3. Журнал

```powershell
Get-Content "$env:ProgramData\NAPS2 WebScan Local Service\logs\service-$(Get-Date -Format yyyy-MM-dd).log" -Encoding UTF8 -Tail 50
```

### 6.4. Проверка сканирования

```powershell
# порт сканера взять из списка в п. 6.2
Invoke-WebRequest http://localhost:9880/eSCL/ScannerCapabilities
```

---

## 7. Подключение сетевого МФУ по адресу

Авто-поиск видит только устройства в своей подсети.
МФУ из другой подсети добавляется вручную по IP-адресу:

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:9899/devices `
  -ContentType "application/json" `
  -Body '{"url":"http://172.17.50.3"}'
```

Достаточно указать адрес устройства. Остальное служба делает сама:

- добавляет к адресу путь `/eSCL`, именно там у МФУ находится eSCL;
- проверяет доступность запросом `GET <url>/ScannerCapabilities` (таймаут 3 с);
- при успехе отвечает `201` и объектом `device`; при ошибке выдает ответ `400` и
  `{"error": "..."}`;
- сохраняет адрес в `custom-scanners.json`, чтобы он пережил перезапуск:

  ```json
  { "scanners": [{ "url": "http://172.17.50.3/eSCL" }] }
  ```

Если МФУ было выключено или сеть недоступна, адрес попадёт в очередь и
подключится автоматически, когда устройство станет доступно (проверка раз в
15 секунд, перезапуск службы не нужен).

Удалить добавленный сканер: остановить службу, удалить запись из
`custom-scanners.json`, запустить службу.

```powershell
sc.exe stop "NAPS2.WebScan Service"
sc.exe start "NAPS2.WebScan Service"
```

---

## 8. Использование из фронтенда

### 8.1. Список сканеров

```ts
const { devices } = await (await fetch("http://localhost:9899/devices")).json();
// [{ id: string, name: string, driver: "Escl" | "Wia", port: number }]
```

### 8.2. Сканирование страницы

`esclUrl` - это путь к eSCL-серверу выбранного сканера, а не адрес самого
сканера: служба раздаёт каждый сканер на своём локальном порту.

```ts
const port = devices[0].port;
// Путь к eSCL-серверу сканера, который подняла служба
const esclUrl = `http://localhost:${port}/eSCL`;

// 1. Возможности сканера (XML)
const caps = await (await fetch(`${esclUrl}/ScannerCapabilities`)).text();

// 2. Запуск задания: тело - XML scan:ScanSettings
const jobResponse = await fetch(`${esclUrl}/ScanJobs`, {
  method: "POST",
  headers: { "Content-Type": "text/xml" },
  body: scanSettingsXml,
});
const jobUrl = jobResponse.headers.get("Location");

// 3. Получить изображение (JPEG или PDF - как указано в настройках)
const image = await (await fetch(`${jobUrl}/NextDocument`)).blob();
```

### 8.3. Несколько страниц с автоподатчика

`NextDocument` вызывается в цикле, пока не вернётся `404`:

```ts
const pages: Blob[] = [];
for (;;) {
  const res = await fetch(`${jobUrl}/NextDocument`);
  if (res.status === 404) break; // страниц больше нет
  if (res.status === 503) {
    // сканер ещё готовит страницу
    await new Promise((r) => setTimeout(r, 2000));
    continue;
  }
  pages.push(await res.blob());
}
```

### 8.4. Сайт на HTTPS

Служба работает только по HTTP, но для сайта на HTTPS сканирование доступно:
`http://localhost` и `http://127.0.0.1` браузеры считают «потенциально
доверенными» адресами. Запросы с HTTPS-страницы к ним **не** блокируются как
mixed content в отличие от `http://` к внешнему серверу.

Что действительно может помешать:

- **Private/Local Network Access в Chrome и Edge.** Перед запросом к локальному
  адресу браузер присылает preflight с заголовком
  `Access-Control-Request-Private-Network: true` и ждёт в ответе
  `Access-Control-Allow-Private-Network: true`. Endpoint `9899` отдаёт CORS
  (`Access-Control-Allow-Origin: *`), а eSCL-раздача на `9880+` включает
  `ServerAllowAnyOrigin`, но этого заголовка не отдаёт ни один из них. Если
  запросы начнут блокироваться, заголовок добавляется в `AddCorsHeaders` в
  `Worker.cs` и служба пересобирается.
- **Корпоративные политики и расширения браузера** могут запрещать обращения к
  localhost.
- **Прямой запрос к МФУ с HTTPS-страницы** (`http://<ip>/eSCL`) - это
  mixed content, и браузер его заблокирует. Поэтому с МФУ общается только
  служба (по HTTP), а браузер - только с localhost.

Быстрая проверка на HTTPS-странице: откройте консоль браузера и выполните

```js
await fetch("http://127.0.0.1:9899/devices").then((r) => r.json());
```

Если ответ пришёл, значит HTTPS-сайт со службой работает.

---

## 9. Настройки

Файл `appsettings.json` лежит рядом с `exe` и читается при старте службы:

```json
{
  "Logging": {
    // Уровни записи в журнал:
    //   Trace/Debug - подробно, Information - обычные события,
    //   Warning - предупреждения, Error - ошибки.
    "LogLevel": {
      "Default": "Information",
      // Сообщения самого ASP.NET/хостинга: только предупреждения и выше
      "Microsoft": "Warning",
      // Старт/остановка службы - полезно видеть всегда
      "Microsoft.Hosting.Lifetime": "Information",
      "NAPS2": "Debug"
    },
    // Настройки файлового журнала
    "File": {
      "KeepDays": 30
    }
  }
}
```

| Ключ                                          | Что делает                                                                                                                       |
| --------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------- |
| `Logging:LogLevel:Default`                    | общий уровень журнала: `Trace`/`Debug` — подробно, `Information` — обычные события, `Warning` — предупреждения, `Error` — ошибки |
| `Logging:LogLevel:Microsoft`                  | сообщения самого ASP.NET/хостинга: только предупреждения и выше                                                                  |
| `Logging:LogLevel:Microsoft.Hosting.Lifetime` | старт и остановка службы — полезно видеть всегда                                                                                 |
| `Logging:LogLevel:NAPS2`                      | логи NAPS2 (поиск сканеров, eSCL, причины ошибок сканирования)                                                                   |
| `Logging:File:KeepDays`                       | сколько дней хранить журнал; меньше 1 заменяется на 30                                                                           |

После изменения перезапустите службу. Порты заданы в коде (`Worker.cs`) и через
настройки не меняются.

---

## 10. Обновление службы

1. Соберите новую версию (`dotnet publish`, раздел 3).
2. Соберите новый установщик (раздел 4.2).
3. Запустите новый `Setup.exe` от администратора.

Установщик сам остановит и удалит старую службу, скопирует файлы и создаст
службу заново. Каталог службы не очищается, поэтому файлы, которых нет в
`publish\`, остаются от прошлой службы: `custom-scanners.json` с добавленными
МФУ сохраняется. `appsettings.json` входит в `publish\`, поэтому при обновлении
заменяется новым файлом из сборки — изменения, внесённые вручную в
установленный файл, нужно внести заново.

---

## 11. Удаление службы

Через установщик: «Параметры» → «Приложения» → «Установленные приложения» →
`NAPS2 WebScan Local Service` → «Удалить». Установщик остановит и удалит службу.

Журналы в `%ProgramData%` удаляются отдельно:

```powershell
Remove-Item "$env:ProgramData\NAPS2 WebScan Local Service" -Recurse -Force
```

---

## 12. Диагностика

| Симптом                                                      | Причина                                                                                                           | Что делать                                                                                                                                     |
| ------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------- |
| `Setup.exe` не запускается, ошибка прав                      | нет прав администратора                                                                                           | запустить «от имени администратора»                                                                                                            |
| Служба не стартует, ошибка `1053`                            | повреждены файлы или не хватает прав                                                                              | переустановить через `Setup.exe`; смотреть «Просмотр событий → Журналы Windows → Приложение»                                                   |
| В журнале `HttpListenerException (183)` для `9899`           | префикс уже занят другим процессом/экземпляром                                                                    | `netstat -ano \| findstr :9899`, `netsh http show urlacl \| findstr 9899`; остановить конфликтующий процесс или перезагрузиться                |
| Порт `9899` не отвечает, сканирование работает               | управляющий сервер не поднялся                                                                                    | см. строки выше; можно обращаться к сканеру напрямую по порту `9880`                                                                           |
| USB-сканер есть в системе, но не находится                   | только TWAIN-драйвер                                                                                              | установить WIA-совместимый драйвер                                                                                                             |
| `POST /devices` → «МФУ не отвечает: HTTP 404»                | устройство использует нестандартный путь eSCL (служба приводит адрес к `/eSCL`)                                   | указать полный eSCL-адрес из документации МФУ                                                                                                  |
| `POST /devices` → «МФУ не отвечает»                          | неверный адрес, МФУ выключено, нет маршрута/VPN, блокирует брандмауэр                                             | проверить `http://<ip>/eSCL/ScannerCapabilities` в браузере; `ping <ip>`                                                                       |
| `POST /devices` → таймаут, хотя из браузера МФУ открывается  | служба не использует системный прокси (намеренно)                                                                 | нужен прямой доступ к устройству; проверить маршрутизацию/VLAN                                                                                 |
| `custom-scanners.json` не сохраняется                        | нет прав на запись в каталог службы                                                                               | служба от `LocalSystem`; переустановить через `Setup.exe`                                                                                      |
| Браузер блокирует запросы с HTTPS-сайта к `http://localhost` | Chrome/Edge включили Private/Local Network Access: нет ответного заголовка `Access-Control-Allow-Private-Network` | проверить в консоли `fetch("http://127.0.0.1:9899/devices")`; при блокировке добавить заголовок в `AddCorsHeaders` (`Worker.cs`) и пересобрать |
| Журнал в «кракозябрах»                                       | UTF-8 читается как ANSI                                                                                           | `Get-Content ... -Encoding UTF8`                                                                                                               |
| Антивирус удаляет/блокирует `exe`                            | самодостаточный файл распаковывается во временный каталог                                                         | добавить в исключения; подписать `exe` сертификатом                                                                                            |
| После перезагрузки служба не поднялась                       | отложенный автозапуск + зависимости                                                                               | `sc.exe qc "NAPS2.WebScan Service"`, проверить `StiSvc` и `RpcSs`                                                                              |

Что приложить к обращению в поддержку:

Перед вводом команд выберите иностранную (английскую) раскладку клавиатуры:
при русской раскладке заглавные буквы стираются.

```powershell
sc.exe qc "NAPS2.WebScan Service"
sc.exe query "NAPS2.WebScan Service"
netstat -ano | findstr ":9880 :9899"
Get-Content "$env:ProgramData\NAPS2 WebScan Local Service\logs\service-$(Get-Date -Format yyyy-MM-dd).log" -Encoding UTF8 -Tail 100
```

---

## 13. Чек-лист

**Сборка:**

- Установлен .NET 8 SDK, `dotnet --version` отвечает
- `dotnet publish` завершился без ошибок, в `publish\` есть `exe` и `appsettings.json`
- Установлен Inno Setup
- `installer.iss` скомпилирован (**Build → Compile**), получен `installer\NAPS2.WebScan.LocalService.Setup.exe`

**Установка:**

- `Setup.exe` запущен от администратора
- `sc.exe query "NAPS2.WebScan Service"` → `RUNNING`
- `http://localhost:9899/devices` отвечает
- Журнал пишется, ошибок старта нет
- Сканер виден: USB через WIA; сетевой авто-поиском или `POST /devices`
- `http://localhost:9880/eSCL/ScannerCapabilities` отвечает
- Кнопка «Сканировать» на сайте работает
