using System.Net;
using System.Text;
using System.Text.Json;
using NAPS2.Escl;
using NAPS2.Escl.Server;
using NAPS2.Images.ImageSharp;
using NAPS2.Remoting.Server;
using NAPS2.Scan;
using NAPS2.Threading;

namespace NAPS2.WebScan.LocalService;

public class Worker : BackgroundService
{
    // Пауза между попытками найти сканер, если сейчас его нет
    private static readonly TimeSpan DeviceSearchInterval = TimeSpan.FromSeconds(15);

    // Таймаут проверки сетевого МФУ по eSCL
    private static readonly TimeSpan NetworkProbeTimeout = TimeSpan.FromSeconds(3);

    // Сколько ждать ответы SSDP при авто-поиске сетевых eSCL-сканеров (мс)
    private static readonly TimeSpan NetworkDiscoveryTimeout = TimeSpan.FromSeconds(5);

    // Первый порт, с которого начинается раздача сканеров по eSCL
    private const int BaseSharedPort = 9880;

    // Порт управляющего HTTP-эндпоинта со списком сканеров для браузера
    private const int DeviceListPort = 9899;

    // Раз в сколько неудачных попыток напоминать в журнале, что сканеров нет
    private const int ReminderEveryAttempts = 20;

    // Сертификаты МФУ обычно самоподписанные: NAPS2 при сканировании тоже их
    // не проверяет (UnverifiedHttpClientHandler в EsclClient), поэтому и
    // проверка доступности не должна требовать доверенный сертификат

    // Используется для проверки сканеров, добавленных по явному адресу
    // (POST /devices)
    private static readonly HttpClient ProbeHttpClient = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
        // Прямое обращение к устройству в локальной сети - без системного прокси
        UseProxy = false
    })
    {
        Timeout = NetworkProbeTimeout
    };

    private readonly ILogger<Worker> _logger;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scanningContext = new ScanningContext(new ImageSharpImageContext());
        var controller = new ScanController(scanningContext);

        using var scanServer = new ScanServer(scanningContext, new EsclServer
        {
            // This line is required for scanning from a browser to work
            SecurityPolicy = EsclSecurityPolicy.ServerAllowAnyOrigin
        });

        // Каталог расшаренных сканеров: каждый сканер получает свой порт
        using var catalog = new SharedDeviceCatalog(scanServer, _logger, BaseSharedPort, ProbeHttpClient);

        // Управляющий эндпоинт: браузер спрашивает, какие сканеры есть
        using var listServer = new DeviceListHttpServer(DeviceListPort, catalog, _logger);
        listServer.Start();

        // Восстанавливаем сканеры, добавленные по адресу оператором в прошлых сеансах
        await catalog.LoadCustomScannersAsync();

        var attempt = 0;
        var firstMissLogged = false;

        try
        {
            // Держим каталог актуальным всё время работы службы: сканеры могут
            // появляться и исчезать (МФУ в сети, USB-подключения) без перезапуска
            while (!stoppingToken.IsCancellationRequested)
            {
                attempt++;

                var devices = await DiscoverDevicesAsync(controller);
                await catalog.SyncAsync(devices);
                // сохранённые адреса, недоступные при старте, 
                // перепроверяем в каждом цикле: когда сеть вернётся,
                // МФУ появится в списке автоматически
                await catalog.RetryPendingAsync();

                if (!catalog.HasDevices && !firstMissLogged)
                {
                    firstMissLogged = true;
                    _logger.LogWarning(
                        "Сканеры не найдены. Повтор каждые {Interval} с, как только сканер появится, служба подхватит его автоматически",
                        DeviceSearchInterval);
                }
                else if (!catalog.HasDevices && attempt % ReminderEveryAttempts == 0)
                {
                    _logger.LogInformation("Сканеры всё ещё не найдены (попытка {Attempt})", attempt);
                }

                try
                {
                    await Task.Delay(DeviceSearchInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            if (catalog.Started)
            {
                await scanServer.Stop();
            }
        }
    }

    /// <summary>
    /// Собирает все доступные сканеры: сетевые eSCL-сканеры (МФУ) по SSDP
    /// и локальные USB-сканеры (WIA). Дубли по ID отбрасываются.
    /// Сканеры, добавленные оператором по явному адресу, живут отдельно
    /// в каталоге и переживают исчезновение сети
    /// </summary>
    private async Task<List<ScanDevice>> DiscoverDevicesAsync(ScanController controller)
    {
        var devices = new List<ScanDevice>();
        var seen = new HashSet<string>();

        foreach (var device in await FindNetworkDevicesByDiscoveryAsync(controller))
        {
            if (seen.Add(device.ID))
            {
                devices.Add(device);
            }
        }

        foreach (var device in await FindLocalDevicesAsync(controller))
        {
            if (seen.Add(device.ID))
            {
                devices.Add(device);
            }
        }

        return devices;
    }

    /// <summary>
    /// Авто-поиск сетевых eSCL-сканеров (МФУ) по SSDP в локальной сети
    /// NAPS2 сам собирает список найденных устройств (имя и адрес каждого МФУ
    /// берутся из его eSCL-ответа)
    /// Возвращает все найденные устройства
    /// </summary>
    private async Task<List<ScanDevice>> FindNetworkDevicesByDiscoveryAsync(ScanController controller)
    {
        try
        {
            var options = new ScanOptions
            {
                Driver = Driver.Escl,
                // Не подхватывать "сканеры" на нашем же IP (например, сам eSCL-сервер)
                ExcludeLocalIPs = true,
                EsclOptions = new EsclOptions
                {
                    SearchTimeout = (int)NetworkDiscoveryTimeout.TotalMilliseconds
                }
            };

            var devices = await controller.GetDeviceList(options);
            if (devices.Count == 0)
            {
                _logger.LogDebug("Сетевые eSCL-сканеры по SSDP не обнаружены (окно поиска {Timeout}), повторю позже",
                    NetworkDiscoveryTimeout.TotalSeconds);
                return new List<ScanDevice>();
            }

            foreach (var device in devices)
            {
                _logger.LogInformation("Обнаружен сетевой eSCL-сканер: {Name} ({Id})", device.Name, device.ID);
            }
            return devices.ToList();
        }
        catch (Exception ex)
        {
            // SSDP может быть заблокирован брандмауэром/политикой - не роняем службу
            _logger.LogDebug(ex, "Не удалось выполнить авто-поиск сетевых eSCL-сканеров");
            return new List<ScanDevice>();
        }
    }

    /// <summary>Все доступные локальные USB-сканеры (WIA)</summary>
    private async Task<List<ScanDevice>> FindLocalDevicesAsync(ScanController controller)
    {
        try
        {
            var devices = await controller.GetDeviceList(Driver.Wia);
            if (devices.Count == 0)
            {
                return new List<ScanDevice>();
            }
            foreach (var device in devices)
            {
                _logger.LogInformation("Локальный USB-сканер: {Name} (ID: {Id})", device.Name, device.ID);
            }
            return devices.ToList();
        }
        catch (Exception ex)
        {
            // WIA может быть недоступно (служба StiSvc не запущена) - это не
            // повод ронять службу сканирования, попробуем в следующем цикле
            _logger.LogDebug(ex, "Не удалось получить список локальных WIA-сканеров");
            return new List<ScanDevice>();
        }
    }

    /// <summary>Приводит URL к виду, который понимает EsclClient. Используется
    /// для адресов, введённых оператором в интерфейсе</summary>
    internal static string? NormalizeUrl(string? url)
    {
        url = url?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }
        // EsclClient собирает адрес как "/{AbsolutePath}/{endpoint}", поэтому
        // завершающий слэш дал бы двойной слэш в пути и 404 на МФУ
        return url.TrimEnd('/');
    }
}

/// <summary>
/// Каталог расшаренных сканеров. Каждый сканер регистрируется в
/// <see cref="ScanServer"/> на своём порту (начиная с basePort), при
/// повторных синхронизациях новые устройства добавляются, исчезнувшие
/// убираются, порты за устройствами сохраняются
///
/// Кроме автоматически найденных устройств (SSDP/WIA) каталог хранит
/// сканеры, добавленные оператором по явному адресу (POST /devices): их
/// синхронизация не удаляет, а адреса сохраняются в custom-scanners.json
/// рядом с исполняемым файлом и восстанавливаются при старте службы

/// Адреса, которые при (пере)запуске были недоступны, 
/// cтавятся в очередь и перепроверяются в каждом цикле
/// синхронизации, пока устройство не станет доступным
/// </summary>
internal sealed class SharedDeviceCatalog : IDisposable
{
    private sealed record Entry(ScanDevice Device, string DisplayName, int Port);

    private readonly ScanServer _scanServer;
    private readonly ILogger _logger;
    private readonly int _basePort;
    private readonly HttpClient _probeClient;
    private readonly string _customFile;

    // Найденные автоматически (SSDP/WIA): каждую синхронизацию сверяются с сетью
    private readonly List<Entry> _entries = new();

    // Добавленные по адресу оператором: синхронизация их не трогает
    private readonly List<Entry> _custom = new();

    // Сохранённые адреса, которые были недоступны на момент (пере)запуска:
    // перепроверяются в каждом цикле синхронизации, пока не станут доступны
    private readonly List<string> _pendingUrls = new();

    private bool _started;

    public SharedDeviceCatalog(ScanServer scanServer, ILogger logger, int basePort, HttpClient probeClient)
    {
        _scanServer = scanServer;
        _logger = logger;
        _basePort = basePort;
        _probeClient = probeClient;
        _customFile = Path.Combine(AppContext.BaseDirectory, "custom-scanners.json");
    }

    public bool Started => _started;

    public bool HasDevices => _entries.Count > 0 || _custom.Count > 0;

    /// <summary>Снимок расшаренных сканеров для управляющего JSON-эндпоинта</summary>
    public IReadOnlyList<SharedDeviceInfo> Snapshot() =>
        _entries.Concat(_custom)
            .Select(e => new SharedDeviceInfo(e.Device.ID, e.DisplayName, e.Device.Driver.ToString(), e.Port))
            .ToList();

    /// <summary>
    /// Добавляет сканер по явному eSCL-адресу (ввёл оператор в браузере).
    /// Адрес проверяется запросом ScannerCapabilities; при успехе устройство
    /// раздаётся на своём порту и адрес сохраняется, чтобы пережить перезапуск
    /// службы
    /// Повторное добавление того же адреса безопасно: вернёт уже раздаваемый
    /// </summary>
    public async Task<AddDeviceResult> AddCustomDeviceAsync(string url)
    {
        url = Worker.NormalizeUrl(url) ?? "";
        if (url.Length == 0)
        {
            return new AddDeviceResult(false, null, "Адрес не задан");
        }

        using var response = await ProbeAsync($"{url}/ScannerCapabilities");
        if (response == null)
        {
            return new AddDeviceResult(false, null, "МФУ не отвечает по eSCL");
        }
        if (!response.IsSuccessStatusCode)
        {
            return new AddDeviceResult(false, null, $"МФУ не отвечает: HTTP {(int)response.StatusCode}");
        }

        // Сканер с таким адресом уже раздаётся, не дублируем, вернём существующий
        var existing = FindByUrl(url);
        if (existing != null)
        {
            _pendingUrls.Remove(url);
            return new AddDeviceResult(true, existing, null);
        }

        var device = new ScanDevice(Driver.Escl, url, url);
        var port = AllocatePort();
        _scanServer.RegisterDevice(device, device.Name, port);
        _custom.Add(new Entry(device, device.Name, port));
        _pendingUrls.Remove(url);
        _logger.LogInformation("Добавлен сканер по адресу: {Url} (порт {Port})", url, port);

        await EnsureStartedAsync();
        await SaveCustomScannersAsync();
        return new AddDeviceResult(true,
            new SharedDeviceInfo(device.ID, device.Name, device.Driver.ToString(), port), null);
    }

    /// <summary>Восстанавливает сохранённые адреса при старте службы</summary>
    public async Task LoadCustomScannersAsync()
    {
        if (!File.Exists(_customFile))
        {
            return;
        }
        try
        {
            var json = await File.ReadAllTextAsync(_customFile);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("scanners", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return;
            }
            foreach (var item in list.EnumerateArray())
            {
                if (!item.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var url = urlEl.GetString();
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }
                var result = await AddCustomDeviceAsync(url);
                if (result.Ok)
                {
                    _logger.LogInformation("Восстановлен сканер по адресу: {Url}", url);
                }
                else
                {
                    _logger.LogWarning(
                        "Сохранённый адрес пока недоступен, поставлю в очередь: {Url} ({Error})",
                        url, result.Error);
                    if (!_pendingUrls.Contains(url))
                    {
                        _pendingUrls.Add(url);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось прочитать файл сохранённых адресов {File}", _customFile);
        }
    }

    /// <summary>
    /// Перепроверяет сохранённые адреса, которые были недоступны на момент
    /// (пере)запуска службы или последней проверки. Вызывается в каждом цикле
    /// синхронизации (раз в 15 с): как только адрес снова отвечает по eSCL,
    /// устройство раздаётся и убирается из очереди. Благодаря этому сетевой
    /// сканер (МФУ) появляется в списке сам, когда сеть/VPN вернулись, без
    /// перезапуска службы и без повторного ввода адреса в браузере
    /// </summary>
    public async Task RetryPendingAsync()
    {
        foreach (var url in _pendingUrls.ToList())
        {
            if (FindByUrl(url) != null)
            {
                _pendingUrls.Remove(url);
                continue;
            }
            var result = await AddCustomDeviceAsync(url);
            if (result.Ok)
            {
                _logger.LogInformation("Сохранённый адрес снова доступен, сканер подключён: {Url}", url);
            }
        }
    }

    private async Task SaveCustomScannersAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                scanners = _custom.Select(e => new { url = e.Device.ID })
            });
            await File.WriteAllTextAsync(_customFile, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось сохранить адреса сканеров в {File}", _customFile);
        }
    }

    /// <summary>Возвращает уже раздаваемый сканер с таким адресом, если есть</summary>
    private SharedDeviceInfo? FindByUrl(string url) =>
        Snapshot().FirstOrDefault(s => s.Id == url);

    public async Task SyncAsync(List<ScanDevice> devices)
    {
        var newIds = devices.Select(d => d.ID).ToHashSet();

        // Убираем только автоматически найденные сканеры, которые исчезли;
        // сканеры, добавленные по адресу, остаются независимо от сети
        foreach (var gone in _entries.Where(e => !newIds.Contains(e.Device.ID)).ToList())
        {
            _logger.LogWarning("Сканер перестал быть доступен, убираю из раздачи: {Name} (порт {Port})",
                gone.DisplayName, gone.Port);
            _scanServer.UnregisterDevice(gone.Device, gone.DisplayName);
            _entries.Remove(gone);
        }

        // Добавляем новые сканеры на свободные порты
        foreach (var device in devices)
        {
            if (_entries.Any(e => e.Device.ID == device.ID) || _custom.Any(e => e.Device.ID == device.ID))
            {
                continue;
            }
            var port = AllocatePort();
            _scanServer.RegisterDevice(device, device.Name, port);
            _entries.Add(new Entry(device, device.Name, port));
            _logger.LogInformation("Раздаю сканер: {Name} на порту {Port} ({Id})", device.Name, port, device.ID);
        }

        await EnsureStartedAsync();
    }

    /// <summary>eSCL-сервер поднимаем один раз, когда появился хотя бы один сканер</summary>
    private async Task EnsureStartedAsync()
    {
        if (!_started && HasDevices)
        {
            await _scanServer.Start();
            _started = true;
            _logger.LogInformation("eSCL-раздача запущена");
        }
    }

    private async Task<HttpResponseMessage?> ProbeAsync(string url)
    {
        try
        {
            return await _probeClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Проверка доступности сканера не удалась: {Url}", url);
            return null;
        }
    }

    private int AllocatePort()
    {
        var used = _entries.Select(e => e.Port).Concat(_custom.Select(e => e.Port)).ToHashSet();
        var port = _basePort;
        while (used.Contains(port))
        {
            port++;
        }
        return port;
    }

    public void Dispose()
    {
        // Сканеры снимаются при Stop()/Dispose() самого ScanServer
        _entries.Clear();
        _custom.Clear();
    }
}

/// <summary>Описание расшаренного сканера для списка в браузере</summary>
internal sealed record SharedDeviceInfo(string Id, string Name, string Driver, int Port);

/// <summary>Результат добавления сканера по адресу (POST /devices)</summary>
internal sealed record AddDeviceResult(bool Ok, SharedDeviceInfo? Device, string? Error);

/// <summary>
/// Минимальный управляющий HTTP-сервер: отдаёт список расшаренных сканеров
/// браузеру по GET /devices и принимает добавление сканера по явному адресу
/// POST /devices. CORS включён, чтобы фронтенд на другом порту мог работать.
/// Сервер работает даже когда сканеров нет, frontend видит пустой список
/// и сообщает пользователю
/// </summary>
internal sealed class DeviceListHttpServer : IDisposable
{
    // Web-вариант сериализации даёт имена полей в camelCase (id/name/port)
    // именно их ждёт фронтенд (с прежней PascalCase-сериализацией список
    // со сканерами пришёл бы на фронтенд с нечитаемыми полями)
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger _logger;
    private readonly Func<IReadOnlyList<SharedDeviceInfo>> _snapshot;
    private readonly Func<string, Task<AddDeviceResult>> _addCustom;
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;

    public DeviceListHttpServer(int port, SharedDeviceCatalog catalog, ILogger logger)
    {
        _logger = logger;

        // Пробуем сначала "+" (все интерфейсы). Под LocalSystem работает без
        // резервирования URLACL; если нет, то запасной вариант на localhost
        foreach (var prefix in new[] { $"http://+:{port}/", $"http://localhost:{port}/" })
        {
            try
            {
                _listener.Prefixes.Add(prefix);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Не удалось занять префикс {Prefix}", prefix);
            }
        }
        _snapshot = catalog.Snapshot;
        _addCustom = catalog.AddCustomDeviceAsync;
        _port = port;
    }

    private readonly int _port;

    public void Start()
    {
        if (_listener.Prefixes.Count == 0)
        {
            _logger.LogWarning("Не удалось поднять управляющий HTTP-сервер списка сканеров на порту {Port}", _port);
            return;
        }
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
        _logger.LogInformation("Список сканеров доступен: http://localhost:{Port}/devices", _port);
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                // Сервер остановлен — выходим
                break;
            }
            _ = Task.Run(() => RespondAsync(context, cancellationToken), cancellationToken);
        }
    }

    private static async Task<string?> ReadUrlAsync(HttpListenerRequest request)
    {
        try
        {
            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
            {
                return url.GetString()?.Trim();
            }
        }
        catch (JsonException)
        {
            // Не JSON - ниже вернётся null, и фронтенд получит понятную ошибку
        }
        return null;
    }

    private async Task RespondAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.HttpMethod == "OPTIONS")
            {
                // предзапрос CORS перед POST из браузера
                context.Response.StatusCode = 204;
                AddCorsHeaders(context.Response);
                context.Response.Close();
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";

            if (context.Request.HttpMethod == "POST" && path == "/devices")
            {
                var url = await ReadUrlAsync(context.Request);
                if (string.IsNullOrWhiteSpace(url))
                {
                    await WriteJsonAsync(context, 400,
                        new { error = "Поле url обязательно, например {\"url\":\"https://172.17.50.3/eSCL\"}" },
                        cancellationToken);
                    return;
                }
                var result = await _addCustom(url);
                if (!result.Ok || result.Device is null)
                {
                    await WriteJsonAsync(context, 400,
                        new { error = result.Error ?? "Не удалось добавить сканер" }, cancellationToken);
                    return;
                }
                await WriteJsonAsync(context, 201, new { device = result.Device }, cancellationToken);
                return;
            }

            if (path is "/" or "/devices")
            {
                await WriteJsonAsync(context, 200, new { devices = _snapshot() }, cancellationToken);
                return;
            }
            await WriteJsonAsync(context, 404, new { error = "Не найдено" }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ошибка при ответе на запрос списка сканеров");
            try
            {
                context.Response.Close();
            }
            catch
            {
                // игнорируем: ответ уже невозможно завершить
            }
        }
    }

    private async Task WriteJsonAsync(HttpListenerContext context, int statusCode, object value,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        AddCorsHeaders(context.Response);
        await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
        context.Response.Close();
    }

    private static void AddCorsHeaders(HttpListenerResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // Уже остановлен
        }
        _listener.Close();
    }
}