// ============================================================================
// Diagnostics.cs: контекст, в котором работает сканирование
//
// Проверяет то, чего не видно из одного WIA-вызова:
//   - от какой учётной записи и в какой сессии запущен процесс
//     (служба работает под LocalSystem в сессии 0 — это частая причина);
//   - запущены ли StiSvc (WIA) и «NAPS2.WebScan Service», под кем;
//   - что Windows думает о самом устройстве (класс Image, статус PnP);
//   - что служба раздаёт в браузер (GET /devices и ScannerCapabilities);
//   - какие ошибки уже записаны в журнал службы.
// ============================================================================

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WiaProbe;

/// <summary>Сканер, который служба раздаёт по eSCL</summary>
internal sealed record ServiceDeviceInfo(string Id, string Name, string Driver, int Port);

/// <summary>Собранные факты для итогового вывода</summary>
internal sealed class DiagnosticsState
{
    public WindowsServiceInfo? StiSvc { get; set; }
    public WindowsServiceInfo? Service { get; set; }
    public int PnpProblems { get; set; }
    public bool WiaVisible { get; set; }
    public int WiaScanOk { get; set; }
    public int WiaScanFailed { get; set; }
    public int WiaScanSkipped { get; set; }
    public bool ServiceReachable { get; set; }
    public bool EsclCapsOk { get; set; }
    public bool ServiceScanAttempted { get; set; }
    public int ServiceScanPages { get; set; }
    public int LogErrors { get; set; }
    public List<ServiceDeviceInfo> ServiceDevices { get; } = new();
}

internal static class Diagnostics
{
    private static readonly (string Label, Regex Regex)[] LogPatterns =
    {
        ("установка свойств WIA", new Regex(@"Error setting property", RegexOptions.IgnoreCase)),
        ("коррекция DPI", new Regex(@"Correcting DPI", RegexOptions.IgnoreCase)),
        ("ошибки WIA", new Regex(@"WiaException|WIA error|0x8[0-9A-F]{7}", RegexOptions.IgnoreCase)),
        ("отказ Download", new Regex(@"\bDownload\b", RegexOptions.IgnoreCase)),
        ("ошибки eSCL-сервера", new Regex(@"ESCL server error", RegexOptions.IgnoreCase))
    };

    // ------------------------------------------------------------------
    // Среда
    // ------------------------------------------------------------------

    public static void PrintEnvironment()
    {
        Out.Line($"  Дата и время:      {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Out.Line($"  Компьютер:         {Environment.MachineName}");
        Out.Line($"  Пользователь:      {Environment.UserDomainName}\\{Environment.UserName}");
        Out.Line($"  ОС:                {RuntimeInformation.OSDescription} " +
                 $"({Environment.OSVersion.Version}, {(Environment.Is64BitOperatingSystem ? "x64" : "x86")})");
        Out.Line($"  Процесс:           {(Environment.Is64BitProcess ? "x64" : "x86")}, " +
                 $"{RuntimeInformation.FrameworkDescription}");
        Out.Line($"  Сессия Windows:    {Process.GetCurrentProcess().SessionId}, " +
                 $"интерактивный рабочий стол: {(Environment.UserInteractive ? "да" : "нет")}");
        Out.Line($"  Каталог запуска:   {Environment.CurrentDirectory}");
        Out.Line($"  Файл пробника:     {Environment.ProcessPath}");

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            Out.Line($"  Учётная запись:    {identity.Name}");
            Out.Line($"  SYSTEM:            {(identity.IsSystem ? "да" : "нет")}, " +
                     $"администратор: {(principal.IsInRole(WindowsBuiltInRole.Administrator) ? "да" : "нет")}");
        }
        catch (Exception ex)
        {
            Out.Line($"  Учётная запись:    не удалось определить ({ex.GetType().Name}: {ex.Message})");
        }
    }

    // ------------------------------------------------------------------
    // Службы
    // ------------------------------------------------------------------

    public static void PrintService(WindowsServiceInfo info)
    {
        if (!info.Installed)
        {
            var suffix = string.IsNullOrWhiteSpace(info.Error) ? "" : $" ({info.Error})";
            Out.Line($"  {info.Name,-24} НЕ УСТАНОВЛЕНА{suffix}");
            return;
        }

        Out.Line($"  {info.Name,-24} {StateRu(info.State),-12} запуск: {info.StartType,-22} " +
                 $"учётная запись: {info.Account ?? "?"}");
        if (!string.IsNullOrWhiteSpace(info.BinaryPath))
        {
            Out.Line($"  {"",-24} {info.BinaryPath}");
        }

        if (info.ProcessId is { } pid)
        {
            Out.Line($"  {"",-24} процесс PID {pid}");
        }

        if (!string.IsNullOrWhiteSpace(info.Error))
        {
            Out.Line($"  {"",-24} ошибка: {info.Error}");
        }
    }

    private static string StateRu(string state) => state switch
    {
        "RUNNING" => "работает",
        "STOPPED" => "остановлена",
        "START_PENDING" => "запускается",
        "STOP_PENDING" => "останавливается",
        "CONTINUE_PENDING" => "продолжается",
        "PAUSE_PENDING" => "приостанавливается",
        "PAUSED" => "приостановлена",
        _ => state
    };

    // ------------------------------------------------------------------
    // Устройства PnP
    // ------------------------------------------------------------------

    /// <summary>Возвращает число устройств класса Image не в состоянии OK</summary>
    public static async Task<int> PrintPnpDevicesAsync()
    {
        const string script = """
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            $ErrorActionPreference = 'SilentlyContinue'
            $items = Get-PnpDevice -Class Image | Select-Object Status, Present, FriendlyName, InstanceId
            ConvertTo-Json -InputObject @($items) -Depth 3
            """;

        var (exitCode, stdout, stderr) = await RunPowerShellAsync(script, 30000);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            Out.Line($"  не удалось получить список (код {exitCode}): {Collapse(stderr)}");
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(stdout);
            var items = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { document.RootElement };

            if (items.Count == 0)
            {
                Out.Line("  устройств класса Image нет — драйвер сканера не установлен или устройство не подключено");
                return 0;
            }

            var problems = 0;
            foreach (var item in items)
            {
                var status = Text(item, "Status");
                var name = Text(item, "FriendlyName");
                var id = Text(item, "InstanceId");
                var present = Text(item, "Present").Equals("True", StringComparison.OrdinalIgnoreCase);
                var ok = status.Equals("OK", StringComparison.OrdinalIgnoreCase);
                if (present && !ok)
                {
                    problems++;
                }

                var note = present ? "" : " (не подключено)";
                var mark = !present ? "--  " : ok ? "OK  " : "!!  ";
                Out.Line($"  {mark} {status,-8} {name}{note}");
                Out.Line($"       {id}");
            }

            Out.Line("  Примечание: Unknown у отключённого устройства — это нормально;");
            Out.Line("  важно, чтобы ваше МФУ было в состоянии OK и без пометки «не подключено».");
            return problems;
        }
        catch (JsonException ex)
        {
            Out.Line($"  не удалось разобрать ответ PowerShell: {ex.Message}");
            Out.Line($"  {Collapse(stdout)}");
            return 0;
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunPowerShellAsync(
        string script, int timeoutMs)
    {
        var startInfo = new ProcessStartInfo(
            "powershell.exe",
            "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false)
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return (-1, "", "не удалось запустить powershell.exe");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(script);
            process.StandardInput.Close();

            using var timeout = new CancellationTokenSource(timeoutMs);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // процесс уже завершился
                }

                return (-1, await stdoutTask, await stderrTask + " (таймаут)");
            }

            return (process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (Exception ex)
        {
            return (-1, "", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Text(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return "";
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.Number => value.ToString(),
            _ => ""
        };
    }

    // ------------------------------------------------------------------
    // eSCL-раздача службы
    // ------------------------------------------------------------------

    public static async Task<(bool Reachable, bool CapsOk)> PrintServiceHttpAsync(
        HttpClient http, int servicePort, DiagnosticsState state)
    {
        var url = $"http://127.0.0.1:{servicePort}/devices";
        string text;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await http.GetAsync(url, timeout.Token);
            text = await response.Content.ReadAsStringAsync(timeout.Token);
            Out.Line($"  GET {url}: HTTP {(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode)
            {
                return (false, false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            Out.Line($"  GET {url}: служба не отвечает ({ex.GetType().Name})");
            return (false, false);
        }

        var devices = ParseDevices(text);
        if (devices.Count == 0)
        {
            Out.Line("  служба отвечает, но в списке нет ни одного сканера");
            return (true, false);
        }

        foreach (var device in devices)
        {
            Out.Line($"  сканер: {device.Name} — порт {device.Port}, драйвер {device.Driver}");
            state.ServiceDevices.Add(device);
        }

        var capsOk = false;
        foreach (var device in devices)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var caps = await EsclClient.GetCapabilitiesAsync(http, device.Port, timeout.Token);
            if (caps == null)
            {
                Out.Line($"  порт {device.Port}: ScannerCapabilities не отвечает");
                continue;
            }

            if (caps.Status != 200)
            {
                Out.Line($"  порт {device.Port}: ScannerCapabilities HTTP {caps.Status}");
                continue;
            }

            capsOk = true;
            Out.Line($"  порт {device.Port}: {(string.IsNullOrWhiteSpace(caps.Model) ? "модель не указана" : caps.Model)}");
            Out.Line($"    цвет: {Join(caps.ColorModes)}; источник: {Join(caps.InputSources)}");
            if (caps.ColorModes.Count == 1 && caps.ColorModes[0].Equals("RGB24", StringComparison.OrdinalIgnoreCase))
            {
                Out.Line("    примечание: объявлен только RGB24 — известное ограничение NAPS2 1.1.6");
            }
        }

        return (true, capsOk);
    }

    private static List<ServiceDeviceInfo> ParseDevices(string json)
    {
        var devices = new List<ServiceDeviceInfo>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("devices", out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                return devices;
            }

            foreach (var element in array.EnumerateArray())
            {
                var port = element.TryGetProperty("port", out var value) &&
                           value.ValueKind == JsonValueKind.Number &&
                           value.TryGetInt32(out var number)
                    ? number
                    : 0;
                if (port <= 0)
                {
                    continue;
                }

                devices.Add(new ServiceDeviceInfo(
                    Text(element, "id"), Text(element, "name"), Text(element, "driver"), port));
            }
        }
        catch (JsonException)
        {
            // Старая служба без /devices: раздел просто будет пустым
        }

        return devices;
    }

    private static string Join(IReadOnlyList<string> values) =>
        values.Count == 0 ? "не указано" : string.Join(", ", values);

    // ------------------------------------------------------------------
    // Журнал службы
    // ------------------------------------------------------------------

    public static int PrintServiceLogs(string? serviceBinaryPath)
    {
        var directories = new List<string>();
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(programData))
        {
            directories.Add(Path.Combine(programData, "NAPS2 WebScan Local Service", "logs"));
        }

        if (!string.IsNullOrWhiteSpace(serviceBinaryPath))
        {
            var binary = serviceBinaryPath.Trim().Trim('"');
            var directory = Path.GetDirectoryName(binary);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                directories.Add(Path.Combine(directory, "logs"));
            }
        }

        directories.Add(Path.Combine(AppContext.BaseDirectory, "logs"));

        var files = new List<FileInfo>();
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                Out.Line($"  каталог: {directory}");
                files.AddRange(new DirectoryInfo(directory)
                    .GetFiles("service-*.log")
                    .OrderByDescending(file => file.LastWriteTime)
                    .Take(2));
            }
            catch (Exception ex)
            {
                Out.Line($"  каталог {directory}: {ex.Message}");
            }
        }

        if (files.Count == 0)
        {
            Out.Line("  файлов журнала не найдено (служба ещё не писала или нет прав на чтение)");
            return 0;
        }

        var errors = 0;
        foreach (var file in files.OrderByDescending(file => file.LastWriteTime).Take(3))
        {
            Out.Line();
            Out.Line($"  файл: {file.FullName} " +
                     $"({file.Length / 1024.0:0.#} КБ, изменён {file.LastWriteTime:dd.MM.yyyy HH:mm:ss})");

            string[] lines;
            try
            {
                // Служба пишет файл прямо сейчас: FileShare.ReadWrite обязателен,
                // иначе сегодняшний журнал всегда «занят другим процессом».
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true);
                lines = reader.ReadToEnd()
                    .Split('\n')
                    .Select(line => line.TrimEnd('\r'))
                    .ToArray();
            }
            catch (Exception ex)
            {
                Out.Line($"    не удалось прочитать: {ex.Message}");
                continue;
            }

            if (lines.All(string.IsNullOrWhiteSpace))
            {
                Out.Line("    файл пока пуст");
                continue;
            }

            foreach (var (label, regex) in LogPatterns)
            {
                var matches = lines.Where(line => regex.IsMatch(line)).ToList();
                if (matches.Count == 0)
                {
                    continue;
                }

                if (label is "установка свойств WIA" or "ошибки WIA")
                {
                    errors += matches.Count;
                }

                Out.Line($"    [{label}] совпадений: {matches.Count}");
                foreach (var line in matches.TakeLast(6))
                {
                    Out.Line($"      {Shorten(line.Trim(), 220)}");
                }
            }

            var tail = lines.Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(8).ToList();
            if (tail.Count > 0)
            {
                Out.Line("    последние строки:");
                foreach (var line in tail)
                {
                    Out.Line($"      {Shorten(line.Trim(), 220)}");
                }
            }
        }

        return errors;
    }

    // ------------------------------------------------------------------
    // Итог
    // ------------------------------------------------------------------

    public static void PrintVerdict(DiagnosticsState state)
    {
        Out.Line("Факты:");
        Out.Line($"  {(state.StiSvc?.Running == true ? "+" : "-")} StiSvc (WIA): " +
                 $"{(state.StiSvc?.Installed == true ? StateRu(state.StiSvc.State) : "не установлена")}");
        Out.Line($"  {(state.Service?.Running == true ? "+" : "-")} NAPS2.WebScan Service: " +
                 $"{(state.Service?.Installed == true ? $"{StateRu(state.Service.State)}, {state.Service.Account}" : "не установлена")}");
        Out.Line($"  {(state.PnpProblems == 0 ? "+" : "-")} устройства класса Image с ошибками PnP: {state.PnpProblems}");
        Out.Line($"  {(state.WiaVisible ? "+" : "-")} WIA-устройства видны: {(state.WiaVisible ? "да" : "нет")}");

        if (state.WiaScanOk + state.WiaScanFailed + state.WiaScanSkipped > 0)
        {
            Out.Line($"  {(state.WiaScanFailed == 0 && state.WiaScanOk > 0 ? "+" : "-")} сканирование через WIA: " +
                     $"успешно {state.WiaScanOk}, отказов {state.WiaScanFailed}, пропущено {state.WiaScanSkipped}");
        }
        else
        {
            Out.Line("  · сканирование через WIA не запускалось (--no-scan или устройство не найдено)");
        }

        Out.Line($"  {(state.ServiceReachable ? "+" : "-")} служба отвечает на /devices: " +
                 $"{(state.ServiceReachable ? "да" : "нет")}");
        Out.Line($"  {(state.EsclCapsOk ? "+" : "-")} eSCL ScannerCapabilities: " +
                 $"{(state.EsclCapsOk ? "да" : "нет")}");

        if (state.ServiceScanAttempted)
        {
            Out.Line($"  {(state.ServiceScanPages > 0 ? "+" : "-")} сканирование через службу (eSCL): " +
                     $"{(state.ServiceScanPages > 0 ? $"страниц {state.ServiceScanPages}" : "страниц нет")}");
        }

        Out.Line($"  {(state.LogErrors == 0 ? "+" : "-")} ошибок WIA в журнале службы: {state.LogErrors}");

        Out.Line();
        Out.Line("Что это значит:");

        if (state.Service?.Installed != true)
        {
            Out.Line("  - Служба NAPS2.WebScan не установлена. Сначала установите Setup.exe,");
            Out.Line("    иначе браузер не увидит сканер.");
        }
        else if (state.Service.Running != true)
        {
            Out.Line("  - Служба NAPS2.WebScan остановлена. Запустите её: sc start \"NAPS2.WebScan Service\"");
        }

        if (state.StiSvc?.Running != true)
        {
            Out.Line("  - StiSvc (служба образов Windows) не работает — без неё WIA не видит сканер.");
            Out.Line("    Проверьте: sc query StiSvc, затем sc start StiSvc.");
        }

        if (state.PnpProblems > 0)
        {
            Out.Line("  - Есть подключённые устройства класса Image не в состоянии OK.");
            Out.Line("    Если это ваше МФУ — Диспетчер устройств, драйвер Kyocera, USB-кабель/порт.");
        }

        if (!state.WiaVisible)
        {
            Out.Line("  - WIA не видит ни одного сканера. Это до драйвера и до службы:");
            Out.Line("    проверьте Диспетчер устройств, драйвер Kyocera, USB и StiSvc.");
        }
        else if (state.WiaScanFailed > 0 && state.WiaScanOk == 0)
        {
            Out.Line("  - Драйвер виден и принимает настройки, но отказывает в передаче изображения");
            Out.Line("    (WIA error 80004005/E_FAIL). Это отказ драйвера или устройства, а не размера");
            Out.Line("    страницы. Проверьте: устройство не занято другой программой, шнур USB,");
            Out.Line("    перезагрузка МФУ, свежий драйвер Kyocera.");
        }
        else if (state.WiaScanFailed > 0)
        {
            Out.Line("  - Часть комбинаций WIA работает, часть падает: смотрите, что именно упало");
            Out.Line("    (версия WIA, размер, DPI, цвет) — строка FAIL в разделе сканирования.");
        }
        else if (state.WiaScanOk > 0 && state.ServiceScanAttempted && state.ServiceScanPages == 0)
        {
            Out.Line("  - Через WIA сканирование работает, а через службу нет: причина в контексте");
            Out.Line("    службы (LocalSystem, сессия 0) или в eSCL-обвязке, а не в драйвере.");
        }
        else if (state.WiaScanOk > 0 && state.ServiceScanPages > 0)
        {
            Out.Line("  - Оба пути работают. Отказ в браузере может быть связан с настройками");
            Out.Line("    клиента (размер страницы, DPI, цвет, выбор устройства) — сравните с этим отчётом.");
        }
        else if (!state.ServiceScanAttempted)
        {
            Out.Line("  - Сканирование не запускалось (--no-scan). Для полной проверки запустите");
            Out.Line("    WiaProbe.exe all без этого ключа.");
        }
    }

    // ------------------------------------------------------------------
    // Мелочи
    // ------------------------------------------------------------------

    public static string Collapse(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
