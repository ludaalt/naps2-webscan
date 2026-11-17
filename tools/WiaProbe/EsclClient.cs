// ============================================================================
// EsclClient.cs: проверка службы NAPS2.WebScan по HTTP (eSCL)
//
// Зачем: WiaProbe проверяет WIA-путь, а браузер работает через eSCL-раздачу
// службы. Эти два пути отличаются, и по отдельности они не отвечают на вопрос
// «служба не может или драйвер не может». Здесь повторяется то, что делает
// tools/scan-escl.ps1, но внутри одного exe и с тем же отчётом.
// ============================================================================

using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace WiaProbe;

/// <summary>Краткие возможности сканера, которые отдаёт служба</summary>
internal sealed record EsclCapabilities(
    int Status,
    string Model,
    IReadOnlyList<string> ColorModes,
    IReadOnlyList<string> InputSources);

internal static class EsclClient
{
    private static readonly Regex ModelRegex =
        new(@"<pwg:MakeAndModel>([^<]+)</pwg:MakeAndModel>", RegexOptions.IgnoreCase);

    private static readonly Regex ColorModeRegex =
        new(@"<scan:ColorMode>([^<]+)</scan:ColorMode>", RegexOptions.IgnoreCase);

    private static readonly Regex InputSourceRegex =
        new(@"<pwg:InputSource>([^<]+)</pwg:InputSource>", RegexOptions.IgnoreCase);

    /// <summary>GET /eSCL/ScannerCapabilities. null — порт не отвечает</summary>
    public static async Task<EsclCapabilities?> GetCapabilitiesAsync(
        HttpClient http, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(
                $"http://127.0.0.1:{port}/eSCL/ScannerCapabilities", cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var model = ModelRegex.Match(text) is { Success: true } match ? match.Groups[1].Value.Trim() : "";
            return new EsclCapabilities(
                (int)response.StatusCode,
                model,
                Distinct(ColorModeRegex, text),
                Distinct(InputSourceRegex, text));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Полный проход через службу: POST ScanJobs, затем NextDocument до 404.
    /// Возвращает число полученных страниц (0 — отказ).
    /// </summary>
    public static async Task<int> ScanAsync(
        HttpClient http,
        int port,
        string sizeName,
        int width,
        int height,
        int dpi,
        string colorMode,
        string outDir,
        int timeoutSec,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outDir);
        var baseUrl = $"http://127.0.0.1:{port}";
        var xml = BuildScanSettings(width, height, dpi, "Platen", colorMode);

        HttpResponseMessage job;
        try
        {
            using var content = new StringContent(xml, Encoding.UTF8, "text/xml");
            job = await http.PostAsync($"{baseUrl}/eSCL/ScanJobs", content, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            Out.Line($"  FAIL POST ScanJobs: {ex.GetType().Name}: {ex.Message}");
            return 0;
        }

        using (job)
        {
            if ((int)job.StatusCode != 201)
            {
                var text = await SafeTextAsync(job, cancellationToken);
                Out.Line($"  FAIL POST ScanJobs: HTTP {(int)job.StatusCode} {job.ReasonPhrase}");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    Out.Line($"       {Collapse(text)}");
                }

                return 0;
            }

            var location = job.Headers.Location?.ToString();
            if (string.IsNullOrWhiteSpace(location))
            {
                Out.Line("  FAIL POST ScanJobs: нет заголовка Location");
                return 0;
            }

            var jobUrl = location.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? location.TrimEnd('/')
                : $"{baseUrl}{location}";

            var jobId = jobUrl[(jobUrl.LastIndexOf('/') + 1)..];
            Out.Line($"  задание: {jobId}");

            var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
            var pages = 0;

            while (DateTime.UtcNow < deadline)
            {
                HttpResponseMessage document;
                try
                {
                    document = await http.GetAsync($"{jobUrl}/NextDocument", cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
                {
                    Out.Line($"  FAIL NextDocument: {ex.GetType().Name}: {ex.Message}");
                    return pages;
                }

                using (document)
                {
                    var status = (int)document.StatusCode;
                    if (status == 200)
                    {
                        var bytes = await document.Content.ReadAsByteArrayAsync(cancellationToken);
                        if (bytes.Length == 0)
                        {
                            Out.Line("  FAIL NextDocument: пустой ответ");
                            return pages;
                        }

                        pages++;
                        var extension = Extension(document.Content.Headers.ContentType);
                        var fileName = $"escl-{port}-{sizeName}-{dpi}-{colorMode.ToLowerInvariant()}-p{pages}{extension}";
                        var path = Path.Combine(outDir, fileName);
                        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                        Out.Line($"  OK   страница {pages}: {bytes.Length / 1024.0:0.#} КБ -> {fileName}");
                        continue;
                    }

                    if (status == 404)
                    {
                        if (pages == 0)
                        {
                            Out.Line("  FAIL страниц нет: 404 на первую страницу");
                            return 0;
                        }

                        Out.Line($"  завершено: страниц {pages}");
                        return pages;
                    }

                    if (status == 503)
                    {
                        Out.Line("  занято (503), повтор через 2 с");
                        await Task.Delay(2000, cancellationToken);
                        continue;
                    }

                    Out.Line($"  FAIL NextDocument: HTTP {status} {document.ReasonPhrase}");
                    if (status == 500)
                    {
                        var details = await TryGetErrorDetailsAsync(http, jobUrl, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(details))
                        {
                            Out.Line($"  ErrorDetails: {Collapse(details)}");
                        }
                    }

                    return pages;
                }
            }

            Out.Line($"  FAIL таймаут {timeoutSec} с: страниц {pages}");
            return pages;
        }
    }

    private static async Task<string?> TryGetErrorDetailsAsync(
        HttpClient http, string jobUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var details = await http.GetAsync($"{jobUrl}/ErrorDetails", cancellationToken);
            return await SafeTextAsync(details, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<string> SafeTextAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            return "";
        }
    }

    private static string Extension(MediaTypeHeaderValue? contentType)
    {
        var mediaType = contentType?.MediaType ?? "";
        if (mediaType.Contains("png", StringComparison.OrdinalIgnoreCase))
        {
            return ".png";
        }

        if (mediaType.Contains("pdf", StringComparison.OrdinalIgnoreCase))
        {
            return ".pdf";
        }

        return ".jpg";
    }

    private static IReadOnlyList<string> Distinct(Regex regex, string text) =>
        regex.Matches(text)
            .Select(match => match.Groups[1].Value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string Collapse(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static string BuildScanSettings(int width, int height, int dpi, string source, string colorMode) => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <scan:ScanSettings xmlns:scan="http://schemas.hp.com/imaging/escl/2011/05/03" xmlns:pwg="http://www.pwg.org/schemas/2010/12/sm">
          <pwg:Version>2.0</pwg:Version>
          <scan:Intent>Document</scan:Intent>
          <pwg:ScanRegions>
            <pwg:ScanRegion>
              <pwg:Height>{height}</pwg:Height>
              <pwg:Width>{width}</pwg:Width>
              <pwg:XOffset>0</pwg:XOffset>
              <pwg:YOffset>0</pwg:YOffset>
            </pwg:ScanRegion>
          </pwg:ScanRegions>
          <pwg:InputSource>{source}</pwg:InputSource>
          <scan:ColorMode>{colorMode}</scan:ColorMode>
          <scan:XResolution>{dpi}</scan:XResolution>
          <scan:YResolution>{dpi}</scan:YResolution>
        </scan:ScanSettings>
        """;
}
