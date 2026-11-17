// ============================================================================
// Program.cs: консольный пробник WIA для диагностики сканеров
//
// Что делает:
//   1. list    — перечисляет WIA-устройства так же, как это делает служба
//                (через NAPS2.Sdk 1.1.6 / WIA 2.0 и WIA 1.0);
//   2. dump    — печатает свойства WIA-устройства и его item'ов: какие размеры,
//                разрешения и глубины цвета реально объявляет драйвер, и какая
//                рамка получится при запросе Letter/A4 на 200/300 dpi;
//   3. scan    — прогоняет матрицу сканирований (WIA 2.0/1.0 × Letter/A4 ×
//                200/300 dpi × Color/Grayscale) и показывает, какая комбинация
//                падает и с каким HRESULT;
//   4. service — контекст: службы Windows, PnP-устройства, ответ службы
//                NAPS2.WebScan и её журнал;
//   5. escl    — проверяет службу так, как это делает браузер (eSCL);
//   6. all     — всё перечисленное подряд и один отчёт report.txt в UTF-8.
//
// Зачем это нужно: служба сканирует тем же самым кодом NAPS2, поэтому если
// пробник воспроизводит ошибку — дело в драйвере/настройках, а не в службе.
// Если в пробнике всё работает, а через службу нет — разница в контексте
// запуска (служба работает под LocalSystem в сессии 0) или в eSCL-обвязке.
//
// Примеры запуска — в tools/WiaProbe/README.md
// ============================================================================

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using NAPS2.Images;
using NAPS2.Images.ImageSharp;
using NAPS2.Scan;
using NAPS2.Wia;
using BitDepth = NAPS2.Images.BitDepth;
using WiaApiVersion = NAPS2.Scan.WiaApiVersion;
using WiaVersion = NAPS2.Wia.WiaVersion;

namespace WiaProbe;

internal static class Program
{
    // Поддерживаемые размеры страницы: имя для консоли + сам размер
    private static readonly (string Name, PageSize Size)[] KnownSizes =
    {
        ("a4", PageSize.A4),
        ("letter", PageSize.Letter),
        ("legal", PageSize.Legal)
    };

    private static readonly (string Name, BitDepth Depth)[] KnownDepths =
    {
        ("color", BitDepth.Color),
        ("gray", BitDepth.Grayscale),
        ("bw", BitDepth.BlackAndWhite)
    };

    // Размеры eSCL — в 1/300 дюйма, как требует спецификация
    private static readonly (string Name, int Width, int Height)[] EsclRegions =
    {
        ("a4", 2481, 3507),
        ("letter", 2550, 3300),
        ("legal", 2550, 4200)
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            // В консоли Windows без этого русский текст превращается в "?".
            // При перенаправлении в файл кодировку задаёт родительский процесс,
            // поэтому для отчёта используется собственный файл (Out).
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch
        {
            // Перенаправленный вывод или нет консоли — не критично
        }

        Cli cli;
        try
        {
            cli = Cli.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Ошибка в аргументах: {ex.Message}");
            PrintHelp();
            return 2;
        }

        if (cli.Command == "help")
        {
            PrintHelp();
            return 0;
        }

        // all всегда оставляет отчёт: это главный сценарий «прислать результат».
        // Для остальных команд файл создаётся только по --log.
        string? runDirectory = null;
        var logPath = cli.LogPath;
        if (cli.Command == "all")
        {
            if (string.IsNullOrWhiteSpace(logPath))
            {
                runDirectory = Path.Combine(Path.GetFullPath(cli.OutDir),
                    $"wiaprobe-{DateTime.Now:yyyyMMdd-HHmmss}");
                logPath = Path.Combine(runDirectory, "report.txt");
            }
            else
            {
                runDirectory = Path.GetDirectoryName(Path.GetFullPath(logPath));
            }
        }

        try
        {
            Out.Open(logPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось открыть отчёт: {ex.Message}");
            Out.Close();
        }

        try
        {
            Out.Line($"WiaProbe: процесс {(Environment.Is64BitProcess ? "x64" : "x86")}, " +
                     $"ОС {Environment.OSVersion.Version}, пользователь {Environment.UserName}");
            if (Out.FilePath != null)
            {
                Out.Line($"Отчёт: {Out.FilePath}");
            }

            return await RunAsync(cli, runDirectory);
        }
        finally
        {
            if (Out.FilePath != null)
            {
                Out.Line();
                Out.Line($"Отчёт сохранён: {Out.FilePath}");
            }

            Out.Close();

            // Двойной клик по exe: окно консоли закрывается сразу после вывода,
            // поэтому без аргументов ждём Enter.
            if (args.Length == 0 && !Console.IsInputRedirected)
            {
                Console.WriteLine();
                Console.Write("Нажмите Enter, чтобы закрыть окно…");
                Console.ReadLine();
            }
        }
    }

    private static async Task<int> RunAsync(Cli cli, string? runDirectory)
    {
        switch (cli.Command)
        {
            case "list":
                return await ListDevicesAsync(cli);
            case "dump":
                return DumpDevices(cli);
            case "scan":
                var scan = await ScanAsync(cli, cli.OutDir);
                return scan.Failed == 0 ? 0 : 1;
            case "service":
                return await ServiceCommandAsync(cli);
            case "escl":
                return await EsclCommandAsync(cli, cli.OutDir);
            case "all":
                return await AllAsync(cli, runDirectory ?? Path.GetFullPath(cli.OutDir));
            default:
                Console.Error.WriteLine($"Неизвестная команда: {cli.Command}");
                PrintHelp();
                return 2;
        }
    }

    // ------------------------------------------------------------------
    // list
    // ------------------------------------------------------------------

    private static async Task<int> ListDevicesAsync(Cli cli)
    {
        using var loggerFactory = CreateLoggerFactory(cli.Verbose);
        using var scanningContext = new ScanningContext(new ImageSharpImageContext())
        {
            Logger = loggerFactory.CreateLogger("NAPS2.Scan")
        };
        var controller = new ScanController(scanningContext);

        var found = 0;
        foreach (var api in cli.Versions)
        {
            Out.Line();
            Out.Line($"WIA {VersionName(api)}:");
            try
            {
                var options = new ScanOptions
                {
                    Driver = Driver.Wia,
                    WiaOptions = new WiaOptions { WiaApiVersion = api }
                };
                var devices = await controller.GetDeviceList(options);
                if (devices.Count == 0)
                {
                    Out.Line("  устройств нет");
                    continue;
                }

                foreach (var device in devices)
                {
                    found++;
                    Out.Line($"  {device.Name}");
                    Out.Line($"    ID: {device.ID}");
                }
            }
            catch (Exception ex)
            {
                Out.Line($"  ошибка получения списка: {Describe(ex)}");
            }
        }

        Out.Line();
        Out.Line(found == 0
            ? "Итог: ни одного WIA-устройства. Проверьте службу StiSvc, драйвер и подключение по USB."
            : $"Итог: устройств найдено {found}.");
        return found == 0 ? 1 : 0;
    }

    // ------------------------------------------------------------------
    // dump
    // ------------------------------------------------------------------

    private static int DumpDevices(Cli cli)
    {
        var any = false;
        foreach (var api in cli.Versions)
        {
            var version = api == WiaApiVersion.Wia10 ? WiaVersion.Wia10 : WiaVersion.Wia20;
            Out.Line();
            Out.Line($"=== WIA {VersionName(api)} ===");
            try
            {
                using var manager = new WiaDeviceManager(version);
                var infos = manager.GetDeviceInfos().ToList();
                if (infos.Count == 0)
                {
                    Out.Line("  устройств нет");
                    continue;
                }

                foreach (var info in infos)
                {
                    using (info)
                    {
                        string id;
                        string name;
                        try
                        {
                            id = info.Id();
                            name = info.Name();
                        }
                        catch (Exception ex)
                        {
                            Out.Line($"  (не удалось прочитать Id/Name: {Describe(ex)})");
                            continue;
                        }

                        Out.Line();
                        Out.Line($"  {name}");
                        Out.Line($"    ID: {id}");
                        if (!Matches(cli.Device, name, id))
                        {
                            Out.Line("    (пропущено: не совпало с --device)");
                            continue;
                        }

                        any = true;
                        using var device = manager.FindDevice(id);
                        DumpProperties("устройство", device.Properties);

                        foreach (var item in device.GetSubItems())
                        {
                            using (item)
                            {
                                Out.Line();
                                Out.Line($"    item: {SafeString(() => item.Name())}");
                                DumpProperties("item", item.Properties);
                                PrintFrameMath(version, device, item, cli);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Out.Line($"  ошибка: {Describe(ex)}");
            }
        }

        if (!any)
        {
            Out.Line();
            Out.Line("Подходящее устройство не найдено. Уточните --device <часть имени или ID>.");
            return 1;
        }

        return 0;
    }

    private static void DumpProperties(string owner, WiaPropertyCollection properties)
    {
        Out.Line($"    свойства ({owner}):");
        List<WiaProperty> list;
        try
        {
            list = properties.OrderBy(p => p.Id).ToList();
        }
        catch (Exception ex)
        {
            Out.Line($"      не удалось перечислить: {Describe(ex)}");
            return;
        }

        foreach (var prop in list)
        {
            string value;
            try
            {
                value = prop.Value switch
                {
                    null => "null",
                    string s => $"\"{s}\"",
                    var v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
                };
            }
            catch (Exception ex)
            {
                value = $"<{ex.GetType().Name}>";
            }

            var attributes = "";
            try
            {
                var a = prop.Attributes;
                attributes = $" | {a.Flags} min={a.Min} nom={a.Nom} max={a.Max} step={a.Step}";
                if (a.Values is { Length: > 0 })
                {
                    attributes += $" values=[{string.Join(", ", a.Values)}]";
                }
            }
            catch
            {
                // У некоторых свойств атрибуты недоступны — это нормально
            }

            Out.Line($"      id={prop.Id,-5} {prop.Name,-26} type={prop.Type,-3} {value}{attributes}");
        }
    }

    /// <summary>
    /// Повторяет расчёт рамки из NAPS2.Sdk 1.1.6 (WiaScanDriver.ConfigureProps):
    /// какие XEXTENT/YEXTENT уйдут драйверу для каждого размера/разрешения.
    /// </summary>
    private static void PrintFrameMath(WiaVersion version, WiaDevice device, WiaItem item, Cli cli)
    {
        int? maxH;
        int? maxV;
        if (version == WiaVersion.Wia10)
        {
            maxH = TryGetInt(device.Properties, WiaPropertyId.DPS_HORIZONTAL_BED_SIZE);
            maxV = TryGetInt(device.Properties, WiaPropertyId.DPS_VERTICAL_BED_SIZE);
        }
        else
        {
            maxH = TryGetInt(item.Properties, WiaPropertyId.IPS_MAX_HORIZONTAL_SIZE);
            maxV = TryGetInt(item.Properties, WiaPropertyId.IPS_MAX_VERTICAL_SIZE);
        }

        Out.Line($"    область сканера (1/1000 дюйма): " +
                 $"{(maxH?.ToString() ?? "нет")} x {(maxV?.ToString() ?? "нет")}");
        if (maxH == null || maxV == null)
        {
            Out.Line("      ВНИМАНИЕ: свойства максимального размера нет. NAPS2.Sdk 1.1.6 " +
                     "не умеет это пережить (GetScanArea бросает KeyNotFoundException), " +
                     "поэтому сканирование упадёт ещё до передачи изображения.");
        }

        foreach (var (name, size) in cli.Sizes)
        {
            foreach (var dpi in cli.Dpis)
            {
                var pageWidth = size.WidthInThousandthsOfAnInch * dpi / 1000;
                var pageHeight = size.HeightInThousandthsOfAnInch * dpi / 1000;
                var note = "";

                if (maxH is > 0)
                {
                    var maxPx = maxH.Value * dpi / 1000;
                    if (pageWidth > maxPx)
                    {
                        note += $", ширина обрезана до {maxPx}";
                        pageWidth = maxPx;
                    }
                }

                if (maxV is > 0)
                {
                    var maxPx = maxV.Value * dpi / 1000;
                    if (pageHeight > maxPx)
                    {
                        note += $", высота обрезана до {maxPx}";
                        pageHeight = maxPx;
                    }
                }

                Out.Line($"      {name,-6} {size,-12} @ {dpi,4} dpi -> " +
                         $"XEXTENT={pageWidth}, YEXTENT={pageHeight}, XPOS=0{note}");
            }
        }
    }

    // ------------------------------------------------------------------
    // scan
    // ------------------------------------------------------------------

    private sealed record ScanResult(int Ok, int Failed, int Skipped);

    private static async Task<ScanResult> ScanAsync(Cli cli, string outDir)
    {
        using var loggerFactory = CreateLoggerFactory(cli.Verbose);
        using var scanningContext = new ScanningContext(new ImageSharpImageContext())
        {
            Logger = loggerFactory.CreateLogger("NAPS2.Scan")
        };
        var controller = new ScanController(scanningContext);

        Directory.CreateDirectory(outDir);
        Out.Line($"Результаты: {outDir}");
        Out.Line($"Матрица: WIA [{string.Join(", ", cli.Versions.Select(VersionName))}], " +
                 $"размер [{string.Join(", ", cli.Sizes.Select(x => x.Name))}], " +
                 $"dpi [{string.Join(", ", cli.Dpis)}], " +
                 $"цвет [{string.Join(", ", cli.Depths.Select(x => x.Name))}]");
        if (cli.NativeUi)
        {
            Out.Line("Режим --native-ui: откроется диалог драйвера, нужен интерактивный рабочий стол.");
        }

        var index = 0;
        var total = cli.Versions.Count * cli.Sizes.Count * cli.Dpis.Count * cli.Depths.Count;
        var ok = 0;
        var failed = 0;
        var skipped = 0;

        foreach (var api in cli.Versions)
        {
            var devices = await controller.GetDeviceList(new ScanOptions
            {
                Driver = Driver.Wia,
                WiaOptions = new WiaOptions { WiaApiVersion = api }
            });

            if (devices.Count == 0)
            {
                Out.Line();
                Out.Line($"[WIA {VersionName(api)}] устройств нет — эта часть матрицы пропущена.");
                skipped += cli.Sizes.Count * cli.Dpis.Count * cli.Depths.Count;
                continue;
            }

            var device = PickDevice(devices, cli.Device);
            if (device == null)
            {
                Out.Line();
                Out.Line($"[WIA {VersionName(api)}] подходящее устройство не выбрано. Найдено:");
                foreach (var d in devices)
                {
                    Out.Line($"  {d.Name}  [{d.ID}]");
                }

                Out.Line("Укажите нужное через --device <часть имени или ID>.");
                skipped += cli.Sizes.Count * cli.Dpis.Count * cli.Depths.Count;
                continue;
            }

            Out.Line();
            Out.Line($"Устройство (WIA {VersionName(api)}): {device.Name}");
            if (string.IsNullOrWhiteSpace(cli.Device) && devices.Count > 1)
            {
                Out.Line("  устройств несколько, беру первое (--device, чтобы выбрать другое)");
            }

            foreach (var (sizeName, size) in cli.Sizes)
            {
                foreach (var dpi in cli.Dpis)
                {
                    foreach (var (depthName, depth) in cli.Depths)
                    {
                        index++;
                        var fileName = $"{index:00}-wia{VersionName(api)}-{sizeName}-{dpi}-{depthName}";
                        Out.Line();
                        Out.Line($"[{index}/{total}] WIA {VersionName(api)}, {sizeName} ({size}), " +
                                 $"{dpi} dpi, {depthName}");

                        var options = new ScanOptions
                        {
                            Driver = Driver.Wia,
                            Device = device,
                            PaperSource = PaperSource.Flatbed,
                            Dpi = dpi,
                            BitDepth = depth,
                            PageSize = size,
                            // Служба выставляет Right при XOffset=0 (см. SnapToAlignment в ScanJob)
                            PageAlign = HorizontalAlign.Right,
                            UseNativeUI = cli.NativeUi,
                            WiaOptions = new WiaOptions { WiaApiVersion = api }
                        };

                        var success = await RunOneScanAsync(controller, options, outDir, fileName, cli.TimeoutSec);
                        if (success)
                        {
                            ok++;
                        }
                        else
                        {
                            failed++;
                        }

                        if (cli.DelayMs > 0 && index < total)
                        {
                            // Даём устройству закрыть задание, иначе следующий запуск
                            // часто получает "занято" и ошибку, не связанную с тестом
                            await Task.Delay(cli.DelayMs);
                        }
                    }
                }
            }
        }

        Out.Line();
        Out.Line($"Итог: успешно {ok}, отказов {failed}, пропущено {skipped} из {total}.");
        return new ScanResult(ok, failed, skipped);
    }

    private static async Task<bool> RunOneScanAsync(ScanController controller, ScanOptions options,
        string outDir, string fileName, int timeoutSec)
    {
        var stopwatch = Stopwatch.StartNew();
        var pages = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
        try
        {
            await foreach (var image in controller.Scan(options, cts.Token).WithCancellation(cts.Token))
            {
                pages++;
                var path = Path.Combine(outDir, $"{fileName}-p{pages}.png");
                using (var stream = File.Create(path))
                {
                    image.Save(stream, ImageFileFormat.Png);
                }

                var size = new FileInfo(path).Length;

                var dimensions = "";
                try
                {
                    using var rendered = image.Render();
                    dimensions = $", {rendered.Width}x{rendered.Height} px";
                }
                catch
                {
                    // Размеры не критичны для диагностики
                }

                image.Dispose();
                stopwatch.Stop();
                Out.Line($"  OK   страница {pages}: {size / 1024.0:0.#} КБ{dimensions}, " +
                         $"{stopwatch.Elapsed.TotalSeconds:0.0} с -> {Path.GetFileName(path)}");
            }

            if (pages == 0)
            {
                stopwatch.Stop();
                Out.Line($"  FAIL страниц нет, задание завершилось без ошибки " +
                         $"({stopwatch.Elapsed.TotalSeconds:0.0} с)");
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            stopwatch.Stop();
            Out.Line($"  FAIL таймаут {timeoutSec} с: задание не завершилось");
            return false;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Out.Line($"  FAIL {Describe(ex)}");
            Out.Line($"       ({stopwatch.Elapsed.TotalSeconds:0.0} с)");
            return false;
        }
    }

    // ------------------------------------------------------------------
    // service — контекст без сканирования
    // ------------------------------------------------------------------

    private static async Task<int> ServiceCommandAsync(Cli cli)
    {
        var state = new DiagnosticsState();

        Out.Section("СРЕДА");
        Diagnostics.PrintEnvironment();

        Out.Section("СЛУЖБЫ");
        state.StiSvc = WindowsServices.Query("StiSvc");
        state.Service = WindowsServices.Query("NAPS2.WebScan Service");
        Diagnostics.PrintService(state.StiSvc);
        Diagnostics.PrintService(state.Service);
        Diagnostics.PrintService(WindowsServices.Query("RpcSs"));

        Out.Section("УСТРОЙСТВА PNP (класс Image)");
        state.PnpProblems = await Diagnostics.PrintPnpDevicesAsync();

        Out.Section("СЛУЖБА NAPS2: СПИСОК УСТРОЙСТВ И eSCL");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var serviceHttp = await Diagnostics.PrintServiceHttpAsync(http, cli.ServicePort, state);
        state.ServiceReachable = serviceHttp.Reachable;
        state.EsclCapsOk = serviceHttp.CapsOk;

        Out.Section("ЖУРНАЛ СЛУЖБЫ NAPS2");
        state.LogErrors = Diagnostics.PrintServiceLogs(state.Service?.BinaryPath);

        Out.Section("ИТОГ");
        Diagnostics.PrintVerdict(state);
        return state.ServiceReachable ? 0 : 1;
    }

    // ------------------------------------------------------------------
    // escl — проверка службы как из браузера
    // ------------------------------------------------------------------

    private static async Task<int> EsclCommandAsync(Cli cli, string outDir)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var targets = new List<(int Port, string Name)>();

        if (cli.EsclPorts.Count > 0)
        {
            targets.AddRange(cli.EsclPorts.Select(port => (port, "")));
        }
        else
        {
            var state = new DiagnosticsState();
            await Diagnostics.PrintServiceHttpAsync(http, cli.ServicePort, state);
            targets.AddRange(state.ServiceDevices.Select(device => (device.Port, device.Name)));

            if (targets.Count == 0)
            {
                Out.Line("Список /devices пуст, проверяю порты 9880–9898...");
                for (var port = 9880; port <= 9898; port++)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    var caps = await EsclClient.GetCapabilitiesAsync(http, port, timeout.Token);
                    if (caps is { Status: 200 })
                    {
                        Out.Line($"  порт {port} отвечает");
                        targets.Add((port, ""));
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(cli.Device))
        {
            targets = targets
                .Where(target => Matches(cli.Device, target.Name, ""))
                .ToList();
        }

        if (targets.Count == 0)
        {
            Out.Line("Сканеры службы не найдены: служба не отвечает или не раздала ни одного устройства.");
            return 1;
        }

        if (cli.NoScan)
        {
            Out.Line("Указан --no-scan: проверены только ScannerCapabilities.");
            return 0;
        }

        Directory.CreateDirectory(outDir);
        Out.Line($"Результаты: {Path.GetFullPath(outDir)}");

        var jobs = 0;
        var failed = 0;
        foreach (var (port, name) in targets)
        {
            Out.Line();
            Out.Line($"=== Порт {port}{(string.IsNullOrWhiteSpace(name) ? "" : $" — {name}")} ===");

            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                var caps = await EsclClient.GetCapabilitiesAsync(http, port, timeout.Token);
                if (caps == null)
                {
                    Out.Line("  ScannerCapabilities: нет ответа");
                    failed++;
                    continue;
                }

                if (caps.Status != 200)
                {
                    Out.Line($"  ScannerCapabilities: HTTP {caps.Status}");
                    failed++;
                    continue;
                }

                Out.Line($"  модель: {(string.IsNullOrWhiteSpace(caps.Model) ? "не указана" : caps.Model)}");
                Out.Line($"  цвет: {JoinOrNone(caps.ColorModes)}; источник: {JoinOrNone(caps.InputSources)}");
            }

            foreach (var (sizeName, size) in cli.Sizes)
            {
                var region = EsclRegion(sizeName);
                if (region == null)
                {
                    continue;
                }

                foreach (var dpi in cli.Dpis)
                {
                    foreach (var (depthName, depth) in cli.Depths)
                    {
                        var colorMode = EsclColorMode(depth);
                        Out.Line();
                        Out.Line($"--- {sizeName.ToUpperInvariant()}: {region.Value.Width}x{region.Value.Height} " +
                                 $"(1/300 дюйма), {dpi} dpi, {colorMode}, Platen ---");
                        jobs++;
                        var pages = await EsclClient.ScanAsync(http, port, sizeName, region.Value.Width,
                            region.Value.Height, dpi, colorMode, outDir, cli.TimeoutSec, CancellationToken.None);
                        if (pages == 0)
                        {
                            failed++;
                        }

                        if (cli.DelayMs > 0)
                        {
                            await Task.Delay(cli.DelayMs);
                        }
                    }
                }
            }
        }

        Out.Line();
        Out.Line(failed == 0
            ? $"Итог: заданий {jobs}, ошибок нет."
            : $"Итог: заданий {jobs}, проблемных {failed}.");
        return failed == 0 ? 0 : 1;
    }

    private static (int Width, int Height)? EsclRegion(string name)
    {
        foreach (var region in EsclRegions)
        {
            if (region.Name == name)
            {
                return (region.Width, region.Height);
            }
        }

        return null;
    }

    private static string EsclColorMode(BitDepth depth) => depth switch
    {
        BitDepth.Color => "RGB24",
        BitDepth.Grayscale => "Grayscale8",
        _ => "BlackAndWhite1"
    };

    private static string JoinOrNone(IReadOnlyList<string> values) =>
        values.Count == 0 ? "не указано" : string.Join(", ", values);

    // ------------------------------------------------------------------
    // all — полная диагностика и отчёт
    // ------------------------------------------------------------------

    private static async Task<int> AllAsync(Cli cli, string runDirectory)
    {
        cli.ApplyAllDefaults();
        var state = new DiagnosticsState();

        Out.Section("1. СРЕДА");
        Diagnostics.PrintEnvironment();

        Out.Section("2. СЛУЖБЫ");
        state.StiSvc = WindowsServices.Query("StiSvc");
        state.Service = WindowsServices.Query("NAPS2.WebScan Service");
        Diagnostics.PrintService(state.StiSvc);
        Diagnostics.PrintService(state.Service);
        Diagnostics.PrintService(WindowsServices.Query("RpcSs"));

        Out.Section("3. УСТРОЙСТВА PNP (класс Image)");
        state.PnpProblems = await Diagnostics.PrintPnpDevicesAsync();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

        Out.Section("4. СЛУЖБА NAPS2: СПИСОК УСТРОЙСТВ И eSCL");
        var serviceHttp = await Diagnostics.PrintServiceHttpAsync(http, cli.ServicePort, state);
        state.ServiceReachable = serviceHttp.Reachable;
        state.EsclCapsOk = serviceHttp.CapsOk;

        Out.Section("5. WIA: СПИСОК УСТРОЙСТВ");
        state.WiaVisible = await ListDevicesAsync(cli) == 0;

        if (state.WiaVisible)
        {
            Out.Section("6. WIA: СВОЙСТВА ДРАЙВЕРА");
            DumpDevices(cli);
        }
        else
        {
            Out.Section("6. WIA: СВОЙСТВА ДРАЙВЕРА");
            Out.Line("Пропущено: WIA-устройство не найдено.");
        }

        if (cli.NoScan)
        {
            Out.Section("7. СКАНИРОВАНИЕ ЧЕРЕЗ WIA");
            Out.Line("Пропущено: указан --no-scan.");
            Out.Section("8. СКАНИРОВАНИЕ ЧЕРЕЗ СЛУЖБУ (eSCL)");
            Out.Line("Пропущено: указан --no-scan.");
        }
        else
        {
            Out.Section("7. СКАНИРОВАНИЕ ЧЕРЕЗ WIA");
            if (state.WiaVisible)
            {
                var scan = await ScanAsync(cli, Path.Combine(runDirectory, "wia"));
                state.WiaScanOk = scan.Ok;
                state.WiaScanFailed = scan.Failed;
                state.WiaScanSkipped = scan.Skipped;
            }
            else
            {
                Out.Line("Пропущено: WIA-устройство не найдено.");
            }

            Out.Section("8. СКАНИРОВАНИЕ ЧЕРЕЗ СЛУЖБУ (eSCL)");
            if (state.ServiceDevices.Count == 0)
            {
                Out.Line("Пропущено: служба не раздала ни одного сканера.");
            }
            else
            {
                var target = state.ServiceDevices[0];
                Out.Line($"Устройство: {target.Name} (порт {target.Port}), A4, 200 dpi, RGB24");
                // Пауза после WIA-серии: устройству нужно закрыть прошлое задание
                await Task.Delay(2000);
                state.ServiceScanAttempted = true;
                state.ServiceScanPages = await EsclClient.ScanAsync(http, target.Port, "a4",
                    2481, 3507, 200, "RGB24", Path.Combine(runDirectory, "escl"), cli.TimeoutSec,
                    CancellationToken.None);
            }
        }

        Out.Section("9. ЖУРНАЛ СЛУЖБЫ NAPS2");
        state.LogErrors = Diagnostics.PrintServiceLogs(state.Service?.BinaryPath);

        Out.Section("10. ИТОГ");
        Diagnostics.PrintVerdict(state);

        if (cli.NoScan)
        {
            return state.ServiceReachable ? 0 : 1;
        }

        var wiaOk = !state.WiaVisible || (state.WiaScanFailed == 0 && state.WiaScanOk > 0);
        var serviceOk = !state.ServiceScanAttempted || state.ServiceScanPages > 0;
        return wiaOk && serviceOk ? 0 : 1;
    }

    // ------------------------------------------------------------------
    // Вспомогательное
    // ------------------------------------------------------------------

    private static ILoggerFactory CreateLoggerFactory(bool verbose) =>
        LoggerFactory.Create(builder =>
        {
            // Свой провайдер вместо консольного: журнал NAPS2 попадает
            // и в консоль, и в отчётный файл (видно "Error setting property")
            builder.ClearProviders();
            builder.AddProvider(new OutLoggerProvider(verbose ? LogLevel.Debug : LogLevel.Information));
            builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
        });

    private static ScanDevice? PickDevice(List<ScanDevice> devices, string? filter)
    {
        if (devices.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(filter))
        {
            return devices[0];
        }

        return devices.FirstOrDefault(d => Matches(filter, d.Name, d.ID));
    }

    private static bool Matches(string? filter, string name, string id)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               id.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static int? TryGetInt(WiaPropertyCollection properties, int propId)
    {
        try
        {
            var property = properties.GetOrNull(propId);
            return property?.Value is int value ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static string SafeString(Func<string> getter)
    {
        try
        {
            return getter();
        }
        catch (Exception ex)
        {
            return $"<{ex.GetType().Name}>";
        }
    }

    private static string VersionName(WiaApiVersion api) =>
        api == WiaApiVersion.Wia10 ? "1.0" : "2.0";

    /// <summary>
    /// Печатает всю цепочку исключений: у ошибок NAPS2 причина всегда во внутреннем
    /// исключении (например, ScanDriverUnknownException -> WiaException).
    /// </summary>
    private static string Describe(Exception exception)
    {
        var builder = new StringBuilder();
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (builder.Length > 0)
            {
                builder.Append(" <- ");
            }

            builder.Append(current.GetType().Name).Append(": ").Append(current.Message);
            if (current is WiaException wia)
            {
                builder.Append($" [0x{wia.ErrorCode:X8} {HresultName(wia.ErrorCode)}]");
            }
        }

        return builder.ToString();
    }

    private static string HresultName(uint code) => code switch
    {
        0x80004005 => "E_FAIL — общий отказ драйвера",
        0x80070057 => "E_INVALIDARG — недопустимое значение свойства",
        0x80210001 => "WIA_ERROR_GENERAL_ERROR",
        0x80210002 => "WIA_ERROR_PAPER_JAM",
        0x80210003 => "WIA_ERROR_PAPER_EMPTY",
        0x80210004 => "WIA_ERROR_PAPER_PROBLEM",
        0x80210005 => "WIA_ERROR_OFFLINE",
        0x80210006 => "WIA_ERROR_BUSY",
        0x80210007 => "WIA_ERROR_WARMING_UP",
        0x80210008 => "WIA_ERROR_USER_INTERVENTION",
        0x80210009 => "WIA_ERROR_ITEM_DELETED",
        0x8021000A => "WIA_ERROR_DEVICE_COMMUNICATION",
        0x8021000B => "WIA_ERROR_INVALID_COMMAND",
        0x8021000C => "WIA_ERROR_INCORRECT_HARDWARE_SETTING",
        0x8021000D => "WIA_ERROR_DEVICE_LOCKED",
        0x8021000E => "WIA_ERROR_EXCEPTION_IN_DRIVER",
        0x8021000F => "WIA_ERROR_INVALID_DRIVER_RESPONSE",
        _ => "неизвестный код"
    };

    private static void PrintHelp()
    {
        Console.WriteLine("""
            WiaProbe — диагностика WIA-сканеров тем же кодом, что и в службе NAPS2.WebScan.

            Использование:
              WiaProbe all     [--device <часть имени>] [--no-scan] [--out <папка>] [--verbose]
              WiaProbe service [--service-port <порт>] [--log <файл>]
              WiaProbe escl    [--device <часть имени>] [--escl-port <порт>] [--no-scan]
                               [--size a4,letter] [--dpi 200,300] [--depth color,gray,bw]
              WiaProbe list    [--wia 2|1|both] [--verbose]
              WiaProbe dump    [--device <часть имени или ID>] [--wia 2|1|both]
                               [--size a4,letter] [--dpi 200,300]
              WiaProbe scan    [--device <часть имени или ID>] [--wia 2|1|both]
                               [--size a4,letter] [--dpi 200,300] [--depth color,gray,bw]
                               [--out <папка>] [--delay <мс>] [--timeout <с>]
                               [--native-ui] [--verbose]

            Команды:
              all      полная диагностика: среда, службы, PnP, WIA, eSCL, журнал, сканы.
                       Всегда пишет отчёт report.txt в UTF-8 в папку wiaprobe-ГГГГММДД-ЧЧММСС
              service  только контекст: службы, PnP, ответ службы, журнал (без сканов)
              escl     сканирование через службу так, как это делает браузер
              list     перечислить WIA-устройства (через NAPS2.Sdk)
              dump     свойства устройства и item'ов + расчёт рамки XEXTENT/YEXTENT
              scan     матрица сканирований через WIA с сохранением страниц в PNG

            Ключи:
              --device <текст>     фильтр по имени или ID
              --wia <список>       версии WIA: 2 (по умолчанию), 1, both
              --size <список>      размеры: a4, letter (по умолчанию a4,letter), legal
              --dpi <список>       разрешения, по умолчанию 200
              --depth <список>     color (по умолчанию), gray, bw
              --out <папка>        куда сохранять страницы и отчёт (.\wiaprobe-out)
              --delay <мс>         пауза между сканированиями (по умолчанию 2000)
              --timeout <с>        таймаут одного сканирования (по умолчанию 120)
              --escl-port <порт>   порт eSCL-раздачи вместо поиска через /devices
              --service-port <порт> порт списка сканеров службы (по умолчанию 9899)
              --log <файл>         писать весь вывод в файл (UTF-8 с BOM)
              --no-scan            только проверки без сканирования
              --native-ui          родной диалог драйвера (только в интерактивной сессии)
              --verbose            отладочный журнал NAPS2 (видно "Error setting property")

            Если вывод в консоли выглядит как "╨┐╤А╨╛╤Ж╨╡╤Б╤Б", это не ошибка
            сканера: так PowerShell перекодирует UTF-8. Смотрите report.txt —
            он всегда в UTF-8 и открывается без настроек.

            Примеры:
              .\WiaProbe.exe all
              .\WiaProbe.exe all --no-scan
              .\WiaProbe.exe service --log service.txt
              .\WiaProbe.exe escl

            Запуск из корня репозитория:
              dotnet run --project .\tools\WiaProbe -- all
            """);
    }

    /// <summary>Разобранные аргументы командной строки.</summary>
    private sealed class Cli
    {
        public string Command { get; private set; } = "list";
        public string? Device { get; private set; }
        public List<WiaApiVersion> Versions { get; } = new() { WiaApiVersion.Wia20 };
        public List<(string Name, PageSize Size)> Sizes { get; private set; } =
            new() { ("a4", PageSize.A4), ("letter", PageSize.Letter) };
        public List<int> Dpis { get; private set; } = new() { 200 };
        public List<(string Name, BitDepth Depth)> Depths { get; private set; } =
            new() { ("color", BitDepth.Color) };
        public string OutDir { get; private set; } = Path.Combine(Environment.CurrentDirectory, "wiaprobe-out");
        public string? LogPath { get; private set; }
        public bool Verbose { get; private set; }
        public bool NativeUi { get; private set; }
        public bool NoScan { get; private set; }
        public int DelayMs { get; private set; } = 2000;
        public int TimeoutSec { get; private set; } = 120;
        public int ServicePort { get; private set; } = 9899;
        public List<int> EsclPorts { get; } = new();

        public bool VersionsExplicit { get; private set; }
        public bool SizesExplicit { get; private set; }
        public bool DpisExplicit { get; private set; }
        public bool DepthsExplicit { get; private set; }

        /// <summary>
        /// all по умолчанию проверяет больше комбинаций, чем scan:
        /// обе версии WIA, A4 и три глубины цвета. Явно заданные ключи не трогаем.
        /// </summary>
        public void ApplyAllDefaults()
        {
            if (!VersionsExplicit)
            {
                Versions.Clear();
                Versions.Add(WiaApiVersion.Wia20);
                Versions.Add(WiaApiVersion.Wia10);
            }

            if (!SizesExplicit)
            {
                Sizes = new() { ("a4", PageSize.A4) };
            }

            if (!DepthsExplicit)
            {
                Depths = new()
                {
                    ("color", BitDepth.Color),
                    ("gray", BitDepth.Grayscale),
                    ("bw", BitDepth.BlackAndWhite)
                };
            }

            if (!DpisExplicit)
            {
                Dpis = new() { 200 };
            }
        }

        public static Cli Parse(string[] args)
        {
            var cli = new Cli();
            var positional = new List<string>();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                string Next(string name)
                {
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException($"для {name} не указано значение");
                    }

                    return args[++i];
                }

                switch (arg)
                {
                    case "--device":
                        cli.Device = Next(arg);
                        break;
                    case "--wia":
                        cli.VersionsExplicit = true;
                        cli.Versions.Clear();
                        foreach (var part in Next(arg).Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            cli.Versions.Add(part.Trim().ToLowerInvariant() switch
                            {
                                "1" or "1.0" or "wia10" => WiaApiVersion.Wia10,
                                "2" or "2.0" or "wia20" => WiaApiVersion.Wia20,
                                "both" or "all" => WiaApiVersion.Default,
                                _ => throw new ArgumentException($"неизвестная версия WIA: {part}")
                            });
                        }

                        // "both" — это WIA 2.0 и WIA 1.0 подряд, но Default нам не нужен
                        if (cli.Versions.Remove(WiaApiVersion.Default))
                        {
                            cli.Versions.Insert(0, WiaApiVersion.Wia20);
                            cli.Versions.Add(WiaApiVersion.Wia10);
                        }

                        break;
                    case "--size":
                        cli.SizesExplicit = true;
                        cli.Sizes = Next(arg)
                            .Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(part => part.Trim().ToLowerInvariant())
                            .Select(name =>
                            {
                                foreach (var known in KnownSizes)
                                {
                                    if (known.Name == name)
                                    {
                                        return known;
                                    }
                                }

                                throw new ArgumentException($"неизвестный размер: {name}");
                            })
                            .ToList();
                        break;
                    case "--dpi":
                        cli.DpisExplicit = true;
                        cli.Dpis = Next(arg)
                            .Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(part => int.Parse(part.Trim(), CultureInfo.InvariantCulture))
                            .ToList();
                        break;
                    case "--depth":
                        cli.DepthsExplicit = true;
                        cli.Depths = Next(arg)
                            .Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(part => part.Trim().ToLowerInvariant())
                            .Select(name =>
                            {
                                foreach (var known in KnownDepths)
                                {
                                    if (known.Name == name)
                                    {
                                        return known;
                                    }
                                }

                                throw new ArgumentException($"неизвестная глубина цвета: {name}");
                            })
                            .ToList();
                        break;
                    case "--out":
                        cli.OutDir = Path.GetFullPath(Next(arg));
                        break;
                    case "--log":
                        cli.LogPath = Path.GetFullPath(Next(arg));
                        break;
                    case "--delay":
                        cli.DelayMs = int.Parse(Next(arg), CultureInfo.InvariantCulture);
                        break;
                    case "--timeout":
                        cli.TimeoutSec = int.Parse(Next(arg), CultureInfo.InvariantCulture);
                        break;
                    case "--service-port":
                        cli.ServicePort = int.Parse(Next(arg), CultureInfo.InvariantCulture);
                        break;
                    case "--escl-port":
                        cli.EsclPorts.AddRange(Next(arg)
                            .Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(part => int.Parse(part.Trim(), CultureInfo.InvariantCulture)));
                        break;
                    case "--no-scan":
                        cli.NoScan = true;
                        break;
                    case "--native-ui":
                        cli.NativeUi = true;
                        break;
                    case "--verbose":
                        cli.Verbose = true;
                        break;
                    case "--help":
                    case "-h":
                        return new Cli { Command = "help" };
                    default:
                        if (arg.StartsWith("--", StringComparison.Ordinal))
                        {
                            throw new ArgumentException($"неизвестный ключ: {arg}");
                        }

                        positional.Add(arg);
                        break;
                }
            }

            if (positional.Count > 0)
            {
                cli.Command = positional[0].ToLowerInvariant();
            }

            if (cli.Command is not ("list" or "dump" or "scan" or "service" or "escl" or "all" or "help"))
            {
                throw new ArgumentException($"неизвестная команда: {cli.Command}");
            }

            return cli;
        }
    }
}
