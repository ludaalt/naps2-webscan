using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
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
    private readonly IConfiguration _config;

    public Worker(ILogger<Worker> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
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
        using var catalog = new SharedDeviceCatalog(scanServer, _logger, BaseSharedPort);

        // Управляющий эндпоинт: браузер спрашивает, какие сканеры есть
        using var listServer = new DeviceListHttpServer(DeviceListPort, catalog, _logger);
        listServer.Start();

        var attempt = 0;
        var firstMissLogged = false;

        try
        {
            // Держим каталог актуальным всё время работы службы: сканеры могут
            // появляться и исчезать (МФУ в сети, USB-подключения) без перезапуска
            while (!stoppingToken.IsCancellationRequested)
            {
                attempt++;

                var devices = await DiscoverDevicesAsync(controller, stoppingToken);
                await catalog.SyncAsync(devices);

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
    /// Собирает все доступные сканеры: сперва явный адрес из конфига
    /// (<c>Scanner:Url</c>), если он задан и отвечает, затем все сетевые
    /// eSCL-сканеры (МФУ) по SSDP и затем все локальные USB-сканеры (WIA)
    /// Дубли по ID отбрасываются, список не пуст только при реальных находках
    /// </summary>
    private async Task<List<ScanDevice>> DiscoverDevicesAsync(ScanController controller, CancellationToken stoppingToken)
    {
        var devices = new List<ScanDevice>();
        var seen = new HashSet<string>();

        var url = NormalizeUrl(_config["Scanner:Url"]);
        var name = _config["Scanner:Name"];

        if (url != null)
        {
            var (ok, reason) = await ProbeNetworkScannerAsync(url, stoppingToken);
            if (ok)
            {
                _logger.LogInformation("Сетевой сканер доступен: {Url}", url);
                var pinned = new ScanDevice(Driver.Escl, url, string.IsNullOrWhiteSpace(name) ? url : name!);
                if (seen.Add(pinned.ID))
                {
                    devices.Add(pinned);
                }
            }
            else
            {
                _logger.LogDebug("Сетевой сканер {Url} недоступен: {Reason}", url, reason);
            }
        }

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

    /// <summary>
    /// Проверяет, отвечает ли сетевой МФУ по eSCL
    ///
    /// Любой успешный HTTP-ответ на ScannerCapabilities означает, что адрес
    /// жив и говорит по eSCL; прочие ответы и сетевые ошибки считаются
    /// недоступностью с указанием причины в журнале
    /// </summary>
    private async Task<(bool Ok, string Reason)> ProbeNetworkScannerAsync(string url, CancellationToken stoppingToken)
    {
        try
        {
            using var response = await ProbeHttpClient.GetAsync(
                $"{url}/ScannerCapabilities",
                HttpCompletionOption.ResponseHeadersRead,
                stoppingToken);
            if (response.IsSuccessStatusCode)
            {
                return (true, "");
            }
            return (false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return (false, "служба останавливается");
        }
        catch (TaskCanceledException)
        {
            // HttpClient не ответил за NetworkProbeTimeout - адрес не отвечает
            return (false, $"нет ответа за {NetworkProbeTimeout.TotalSeconds:0} с");
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    /// <summary>Приводит URL из конфига к виду, который понимает EsclClient</summary>
    private static string? NormalizeUrl(string? url)
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
/// </summary>
internal sealed class SharedDeviceCatalog : IDisposable
{
    private sealed record Entry(ScanDevice Device, string DisplayName, int Port);

    private readonly ScanServer _scanServer;
    private readonly ILogger _logger;
    private readonly int _basePort;
    private readonly List<Entry> _entries = new();
    private bool _started;

    public SharedDeviceCatalog(ScanServer scanServer, ILogger logger, int basePort)
    {
        _scanServer = scanServer;
        _logger = logger;
        _basePort = basePort;
    }

    public bool Started => _started;

    public bool HasDevices => _entries.Count > 0;

    /// <summary>Снимок расшаренных сканеров для управляющего JSON-эндпоинта</summary>
    public IReadOnlyList<SharedDeviceInfo> Snapshot() =>
        _entries.Select(e => new SharedDeviceInfo(e.Device.ID, e.DisplayName, e.Device.Driver.ToString(), e.Port)).ToList();

    public async Task SyncAsync(List<ScanDevice> devices)
    {
        var newIds = devices.Select(d => d.ID).ToHashSet();

        // Убираем сканеры, которые исчезли
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
            if (_entries.Any(e => e.Device.ID == device.ID))
            {
                continue;
            }
            var port = AllocatePort();
            _scanServer.RegisterDevice(device, device.Name, port);
            _entries.Add(new Entry(device, device.Name, port));
            _logger.LogInformation("Раздаю сканер: {Name} на порту {Port} ({Id})", device.Name, port, device.ID);
        }

        // eSCL-сервер поднимаем один раз, когда появился хотя бы один сканер
        if (!_started && _entries.Count > 0)
        {
            await _scanServer.Start();
            _started = true;
            _logger.LogInformation("eSCL-раздача запущена: порты {Start}..{End}",
                _entries.Min(e => e.Port), _entries.Max(e => e.Port));
        }
    }

    private int AllocatePort()
    {
        var used = _entries.Select(e => e.Port).ToHashSet();
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
    }
}

/// <summary>Описание расшаренного сканера для списка в браузере</summary>
internal sealed record SharedDeviceInfo(string Id, string Name, string Driver, int Port);

/// <summary>
/// Минимальный управляющий HTTP-сервер: отдаёт список расшаренных сканеров
/// браузеру по GET /devices (с CORS, чтобы фронтенд на другом порту мог
/// его прочитать). Работает даже когда сканеров нет, frontend видит пустой
/// список и сообщает пользователю
/// </summary>
internal sealed class DeviceListHttpServer : IDisposable
{
    private readonly ILogger _logger;
    private readonly Func<IReadOnlyList<SharedDeviceInfo>> _snapshot;
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

    private async Task RespondAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.HttpMethod == "OPTIONS")
            {
                context.Response.StatusCode = 204;
                AddCorsHeaders(context.Response);
                context.Response.Close();
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path is "/" or "/devices")
            {
                var json = JsonSerializer.Serialize(new { devices = _snapshot() });
                var bytes = Encoding.UTF8.GetBytes(json);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                AddCorsHeaders(context.Response);
                await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
            }
            else
            {
                context.Response.StatusCode = 404;
            }
            context.Response.Close();
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
                // Игнорируем: ответ уже невозможно завершить
            }
        }
    }

    private static void AddCorsHeaders(HttpListenerResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
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