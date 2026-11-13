// ============================================================================
// Program.cs: консольный пробник WIA для диагностики сканеров
//
// Что делает:
//   1. list  — перечисляет WIA-устройства так же, как это делает служба
//              (через NAPS2.Sdk 1.1.6 / WIA 2.0 и WIA 1.0);
//   2. dump  — печатает свойства WIA-устройства и его item'ов: какие размеры,
//              разрешения и глубины цвета реально объявляет драйвер, и какая
//              рамка получится при запросе Letter/A4 на 200/300 dpi;
//   3. scan  — прогоняет матрицу сканирований (WIA 2.0/1.0 × Letter/A4 ×
//              200/300 dpi × Color/Grayscale) и показывает, какая комбинация
//              падает и с каким HRESULT.
//
// Зачем это нужно: служба сканирует тем же самым кодом NAPS2, поэтому если
// пробник воспроизводит ошибку — дело в драйвере/настройках, а не в службе.
// Если в пробнике всё работает, а через службу нет — разница в контексте
// запуска (служба работает под LocalSystem в сессии 0).
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

    public static async Task<int> Main(string[] args)
    {
        var exitCode = await RunAsync(args);

        // Двойной клик по exe: окно консоли закрывается сразу после вывода,
        // поэтому без аргументов ждём Enter.
        if (args.Length == 0 && !Console.IsInputRedirected)
        {
            Console.WriteLine();
            Console.Write("Нажмите Enter, чтобы закрыть окно…");
            Console.ReadLine();
        }

        return exitCode;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // Перенаправленный вывод: кодировку менять не нужно
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

        Console.WriteLine($"WiaProbe: процесс {(Environment.Is64BitProcess ? "x64" : "x86")}, " +
                          $"ОС {Environment.OSVersion.Version}, пользователь {Environment.UserName}");

        switch (cli.Command)
        {
            case "list":
                return await ListDevicesAsync(cli);
            case "dump":
                return DumpDevices(cli);
            case "scan":
                return await ScanAsync(cli);
            case "help":
                PrintHelp();
                return 0;
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
            Console.WriteLine();
            Console.WriteLine($"WIA {VersionName(api)}:");
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
                    Console.WriteLine("  устройств нет");
                    continue;
                }
                foreach (var device in devices)
                {
                    found++;
                    Console.WriteLine($"  {device.Name}");
                    Console.WriteLine($"    ID: {device.ID}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ошибка получения списка: {Describe(ex)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(found == 0
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
            Console.WriteLine();
            Console.WriteLine($"=== WIA {VersionName(api)} ===");
            try
            {
                using var manager = new WiaDeviceManager(version);
                var infos = manager.GetDeviceInfos().ToList();
                if (infos.Count == 0)
                {
                    Console.WriteLine("  устройств нет");
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
                            Console.WriteLine($"  (не удалось прочитать Id/Name: {Describe(ex)})");
                            continue;
                        }

                        Console.WriteLine();
                        Console.WriteLine($"  {name}");
                        Console.WriteLine($"    ID: {id}");
                        if (!Matches(cli.Device, name, id))
                        {
                            Console.WriteLine("    (пропущено: не совпало с --device)");
                            continue;
                        }

                        any = true;
                        using var device = manager.FindDevice(id);
                        DumpProperties("устройство", device.Properties);

                        foreach (var item in device.GetSubItems())
                        {
                            using (item)
                            {
                                Console.WriteLine();
                                Console.WriteLine($"    item: {SafeString(() => item.Name())}");
                                DumpProperties("item", item.Properties);
                                PrintFrameMath(version, device, item, cli);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ошибка: {Describe(ex)}");
            }
        }

        if (!any)
        {
            Console.WriteLine();
            Console.WriteLine("Подходящее устройство не найдено. Уточните --device <часть имени или ID>.");
            return 1;
        }
        return 0;
    }

    private static void DumpProperties(string owner, WiaPropertyCollection properties)
    {
        Console.WriteLine($"    свойства ({owner}):");
        List<WiaProperty> list;
        try
        {
            list = properties.OrderBy(p => p.Id).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      не удалось перечислить: {Describe(ex)}");
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

            Console.WriteLine($"      id={prop.Id,-5} {prop.Name,-26} type={prop.Type,-3} {value}{attributes}");
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

        Console.WriteLine($"    область сканера (1/1000 дюйма): " +
                          $"{(maxH?.ToString() ?? "нет")} x {(maxV?.ToString() ?? "нет")}");
        if (maxH == null || maxV == null)
        {
            Console.WriteLine("      ВНИМАНИЕ: свойства максимального размера нет. NAPS2.Sdk 1.1.6 " +
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

                Console.WriteLine($"      {name,-6} {size,-12} @ {dpi,4} dpi -> " +
                                  $"XEXTENT={pageWidth}, YEXTENT={pageHeight}, XPOS=0{note}");
            }
        }
    }

    // ------------------------------------------------------------------
    // scan
    // ------------------------------------------------------------------

    private static async Task<int> ScanAsync(Cli cli)
    {
        using var loggerFactory = CreateLoggerFactory(cli.Verbose);
        using var scanningContext = new ScanningContext(new ImageSharpImageContext())
        {
            Logger = loggerFactory.CreateLogger("NAPS2.Scan")
        };
        var controller = new ScanController(scanningContext);

        Directory.CreateDirectory(cli.OutDir);
        Console.WriteLine($"Результаты: {cli.OutDir}");
        Console.WriteLine($"Матрица: WIA [{string.Join(", ", cli.Versions.Select(VersionName))}], " +
                          $"размер [{string.Join(", ", cli.Sizes.Select(x => x.Name))}], " +
                          $"dpi [{string.Join(", ", cli.Dpis)}], " +
                          $"цвет [{string.Join(", ", cli.Depths.Select(x => x.Name))}]");
        if (cli.NativeUi)
        {
            Console.WriteLine("Режим --native-ui: откроется диалог драйвера, нужен интерактивный рабочий стол.");
        }

        var index = 0;
        var total = cli.Versions.Count * cli.Sizes.Count * cli.Dpis.Count * cli.Depths.Count;
        var failed = 0;

        foreach (var api in cli.Versions)
        {
            var devices = await controller.GetDeviceList(new ScanOptions
            {
                Driver = Driver.Wia,
                WiaOptions = new WiaOptions { WiaApiVersion = api }
            });

            if (devices.Count == 0)
            {
                Console.WriteLine();
                Console.WriteLine($"[WIA {VersionName(api)}] устройств нет — эта часть матрицы пропущена.");
                failed += cli.Sizes.Count * cli.Dpis.Count * cli.Depths.Count;
                continue;
            }

            var device = PickDevice(devices, cli.Device);
            if (device == null)
            {
                Console.WriteLine();
                Console.WriteLine($"[WIA {VersionName(api)}] подходящее устройство не выбрано. Найдено:");
                foreach (var d in devices)
                {
                    Console.WriteLine($"  {d.Name}  [{d.ID}]");
                }
                Console.WriteLine("Укажите нужное через --device <часть имени или ID>.");
                failed += cli.Sizes.Count * cli.Dpis.Count * cli.Depths.Count;
                continue;
            }

            Console.WriteLine();
            Console.WriteLine($"Устройство (WIA {VersionName(api)}): {device.Name}");

            foreach (var (sizeName, size) in cli.Sizes)
            {
                foreach (var dpi in cli.Dpis)
                {
                    foreach (var (depthName, depth) in cli.Depths)
                    {
                        index++;
                        var fileName = $"{index:00}-wia{VersionName(api)}-{sizeName}-{dpi}-{depthName}";
                        Console.WriteLine();
                        Console.WriteLine($"[{index}/{total}] WIA {VersionName(api)}, {sizeName} ({size}), " +
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

                        var ok = await RunOneScanAsync(controller, options, cli.OutDir, fileName, cli.TimeoutSec);
                        if (!ok)
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

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? $"Итог: все {total} комбинаций отработали."
            : $"Итог: неудачных комбинаций {failed} из {total}.");
        return failed == 0 ? 0 : 1;
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
                Console.WriteLine($"  OK   страница {pages}: {size / 1024.0:0.#} КБ{dimensions}, " +
                                  $"{stopwatch.Elapsed.TotalSeconds:0.0} с -> {Path.GetFileName(path)}");
            }

            if (pages == 0)
            {
                stopwatch.Stop();
                Console.WriteLine($"  FAIL страниц нет, задание завершилось без ошибки " +
                                  $"({stopwatch.Elapsed.TotalSeconds:0.0} с)");
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            stopwatch.Stop();
            Console.WriteLine($"  FAIL таймаут {timeoutSec} с: задание не завершилось");
            return false;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Console.WriteLine($"  FAIL {Describe(ex)}");
            Console.WriteLine($"       ({stopwatch.Elapsed.TotalSeconds:0.0} с)");
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Вспомогательное
    // ------------------------------------------------------------------

    private static ILoggerFactory CreateLoggerFactory(bool verbose) =>
        LoggerFactory.Create(builder =>
        {
            builder.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss.fff ";
            });
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
            return devices.Count == 1 ? devices[0] : null;
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
              WiaProbe list  [--wia 2|1|both] [--verbose]
              WiaProbe dump  [--device <часть имени или ID>] [--wia 2|1|both] [--size a4,letter]
                             [--dpi 200,300]
              WiaProbe scan  [--device <часть имени или ID>] [--wia 2|1|both] [--size a4,letter]
                             [--dpi 200,300] [--depth color,gray,bw] [--out <папка>]
                             [--delay <мс>] [--timeout <с>] [--native-ui] [--verbose]

            Команды:
              list   перечислить WIA-устройства (через NAPS2.Sdk)
              dump   свойства устройства и item'ов + расчёт рамки XEXTENT/YEXTENT
              scan   матрица сканирований с сохранением страниц в PNG

            Ключи:
              --device <текст>   фильтр по имени или ID (обязателен, если устройств несколько)
              --wia <список>     версии WIA: 2 (по умолчанию), 1, both
              --size <список>    размеры: a4, letter (по умолчанию a4,letter), legal
              --dpi <список>     разрешения, по умолчанию 200
              --depth <список>   color (по умолчанию), gray, bw
              --out <папка>      куда сохранять страницы (по умолчанию .\wiaprobe-out)
              --delay <мс>       пауза между сканированиями (по умолчанию 2000)
              --timeout <с>      таймаут одного сканирования (по умолчанию 120)
              --native-ui        родной диалог драйвера (только в интерактивной сессии)
              --verbose          включить отладочный журнал NAPS2 (видно "Error setting property")

            Запуск из корня репозитория:
              dotnet run --project .\tools\WiaProbe -- list
              dotnet run --project .\tools\WiaProbe -- dump --device Kyocera --size a4,letter
              dotnet run --project .\tools\WiaProbe -- scan --device Kyocera --dpi 200,300 --verbose
            """);
    }

    /// <summary>Разобранные аргументы командной строки.</summary>
    private sealed class Cli
    {
        public string Command { get; set; } = "list";
        public string? Device { get; private set; }
        public List<WiaApiVersion> Versions { get; } = new() { WiaApiVersion.Wia20 };
        public List<(string Name, PageSize Size)> Sizes { get; private set; } =
            new() { ("a4", PageSize.A4), ("letter", PageSize.Letter) };
        public List<int> Dpis { get; private set; } = new() { 200 };
        public List<(string Name, BitDepth Depth)> Depths { get; private set; } =
            new() { ("color", BitDepth.Color) };
        public string OutDir { get; private set; } = Path.Combine(Environment.CurrentDirectory, "wiaprobe-out");
        public bool Verbose { get; private set; }
        public bool NativeUi { get; private set; }
        public int DelayMs { get; private set; } = 2000;
        public int TimeoutSec { get; private set; } = 120;

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
                        cli.Dpis = Next(arg)
                            .Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(part => int.Parse(part.Trim(), CultureInfo.InvariantCulture))
                            .ToList();
                        break;
                    case "--depth":
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
                    case "--delay":
                        cli.DelayMs = int.Parse(Next(arg), CultureInfo.InvariantCulture);
                        break;
                    case "--timeout":
                        cli.TimeoutSec = int.Parse(Next(arg), CultureInfo.InvariantCulture);
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
            if (cli.Command is not ("list" or "dump" or "scan" or "help"))
            {
                throw new ArgumentException($"неизвестная команда: {cli.Command}");
            }
            return cli;
        }
    }
}
