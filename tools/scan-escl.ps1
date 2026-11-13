# ============================================================================
# scan-escl.ps1: проверка eSCL-пути установленной службы NAPS2.WebScan
#
# Что делает:
#   1. берёт список сканеров у службы (GET http://localhost:9899/devices) —
#      имя, драйвер и порт eSCL-раздачи;
#   2. для каждого сканера отправляет ScanJobs с явно заданным размером
#      страницы: A4 (2481x3507) или Letter (2550x3300) в единицах 1/300 дюйма;
#   3. забирает страницы через NextDocument и сохраняет их в папку;
#   4. при ошибке печатает HTTP-код и ErrorDetails — текст исключения NAPS2.
#
# Зачем: служба по умолчанию подставляет Letter, если клиент не передал
# размер страницы. Скрипт позволяет сравнить A4 и Letter на одном и том же
# устройстве, не пересобирая службу и не меняя фронтенд.
#
# Запуск (Windows PowerShell 5.1 или PowerShell 7):
#   .\tools\scan-escl.ps1
#   .\tools\scan-escl.ps1 -Size a4
#   .\tools\scan-escl.ps1 -Port 9881 -Size a4,letter -Dpi 300
#   .\tools\scan-escl.ps1 -InputSource Feeder -Size a4
# ============================================================================

[CmdletBinding()]
param(
    # Порты eSCL-раздачи. Если не заданы — берутся из /devices,
    # иначе перебираются 9880..9898.
    [int[]]$Port = @(),

    # Размеры страницы для сравнения
    [ValidateSet('a4', 'letter')]
    [string[]]$Size = @('a4', 'letter'),

    [int]$Dpi = 200,

    [ValidateSet('Color', 'Grayscale', 'BlackAndWhite')]
    [string]$ColorMode = 'Color',

    [ValidateSet('Platen', 'Feeder')]
    [string]$InputSource = 'Platen',

    [string]$OutDir = '.\esclprobe-out',

    # Таймаут одного задания, секунды
    [int]$TimeoutSec = 120,

    [string]$Address = '127.0.0.1'
)

$ErrorActionPreference = 'Stop'
$OutDir = [System.IO.Path]::GetFullPath($OutDir)

# Размеры eSCL — в 1/300 дюйма (так требует спецификация)
$Regions = @{
    a4     = @{ Width = 2481; Height = 3507 }
    letter = @{ Width = 2550; Height = 3300 }
}

$ColorModes = @{
    Color         = 'RGB24'
    Grayscale     = 'Grayscale8'
    BlackAndWhite = 'BlackAndWhite1'
}

function Invoke-Http {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Url,
        [byte[]]$Body,
        [string]$ContentType,
        [int]$TimeoutSec = 30
    )

    $request = [System.Net.HttpWebRequest]::Create($Url)
    $request.Method = $Method
    $request.Timeout = $TimeoutSec * 1000
    $request.ReadWriteTimeout = $TimeoutSec * 1000
    $request.AllowAutoRedirect = $false
    $request.KeepAlive = $false
    $request.UserAgent = 'scan-escl/1.0'
    if ($ContentType) {
        $request.ContentType = $ContentType
    }
    if ($null -ne $Body) {
        $request.ContentLength = $Body.Length
        $stream = $request.GetRequestStream()
        try {
            $stream.Write($Body, 0, $Body.Length)
        }
        finally {
            $stream.Dispose()
        }
    }

    $response = $null
    try {
        $response = $request.GetResponse()
    }
    catch [System.Net.WebException] {
        if ($null -eq $_.Exception.Response) {
            throw
        }
        $response = [System.Net.HttpWebResponse]$_.Exception.Response
    }

    try {
        $status = [int]$response.StatusCode

        $headers = @{}
        foreach ($key in $response.Headers.AllKeys) {
            $headers[$key] = $response.Headers[$key]
        }

        $buffer = New-Object System.IO.MemoryStream
        $responseStream = $response.GetResponseStream()
        if ($responseStream) {
            try {
                $responseStream.CopyTo($buffer)
            }
            finally {
                $responseStream.Dispose()
            }
        }
        $bytes = $buffer.ToArray()
        $buffer.Dispose()

        $text = ''
        if ($bytes.Length -gt 0) {
            $encoding = [System.Text.Encoding]::UTF8
            $contentTypeHeader = [string]$headers['Content-Type']
            if ($contentTypeHeader -match 'charset=([^;]+)') {
                try {
                    $encoding = [System.Text.Encoding]::GetEncoding($matches[1].Trim())
                }
                catch {
                    # Неизвестная кодировка — остаёмся на UTF-8
                }
            }
            $text = $encoding.GetString($bytes)
        }

        return [pscustomobject]@{
            Status  = $status
            Headers = $headers
            Bytes   = $bytes
            Text    = $text
        }
    }
    finally {
        if ($response) {
            $response.Dispose()
        }
    }
}

function Test-EsclPort {
    param([int]$PortNumber)

    try {
        $result = Invoke-Http -Method GET `
            -Url "http://${Address}:$PortNumber/eSCL/ScannerCapabilities" `
            -TimeoutSec 5
        return $result.Status -eq 200
    }
    catch {
        return $false
    }
}

function Get-ServiceDevices {
    try {
        $result = Invoke-Http -Method GET -Url "http://${Address}:9899/devices" -TimeoutSec 5
        if ($result.Status -eq 200) {
            $json = $result.Text | ConvertFrom-Json
            if ($null -ne $json.devices) {
                return @($json.devices)
            }
        }
    }
    catch {
        # Служба может быть старой и не иметь эндпоинта /devices
    }
    return @()
}

function Get-XmlValue {
    param([string]$Text, [string]$Tag)

    $match = [regex]::Match($Text, "<$Tag>([^<]*)</$Tag>")
    if ($match.Success) {
        return $match.Groups[1].Value
    }
    return ''
}

function New-ScanSettingsXml {
    param(
        [int]$Width,
        [int]$Height,
        [int]$Resolution,
        [string]$Source,
        [string]$Mode
    )

    return @"
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<scan:ScanSettings xmlns:scan="http://schemas.hp.com/imaging/escl/2011/05/03" xmlns:pwg="http://www.pwg.org/schemas/2010/12/sm">
  <pwg:Version>2.0</pwg:Version>
  <scan:Intent>Document</scan:Intent>
  <pwg:ScanRegions>
    <pwg:ScanRegion>
      <pwg:Height>$Height</pwg:Height>
      <pwg:Width>$Width</pwg:Width>
      <pwg:XOffset>0</pwg:XOffset>
      <pwg:YOffset>0</pwg:YOffset>
    </pwg:ScanRegion>
  </pwg:ScanRegions>
  <pwg:InputSource>$Source</pwg:InputSource>
  <scan:ColorMode>$Mode</scan:ColorMode>
  <scan:XResolution>$Resolution</scan:XResolution>
  <scan:YResolution>$Resolution</scan:YResolution>
</scan:ScanSettings>
"@
}

# ---------------------------------------------------------------------------
# Поиск сканеров
# ---------------------------------------------------------------------------

$targets = @()

if ($Port.Count -gt 0) {
    foreach ($portNumber in $Port) {
        $targets += [pscustomobject]@{ Port = $portNumber; Name = '' }
    }
}
else {
    $devices = Get-ServiceDevices
    if ($devices.Count -gt 0) {
        Write-Host 'Сканеры службы (GET http://localhost:9899/devices):'
        foreach ($device in $devices) {
            Write-Host ('  {0} — порт {1}, драйвер {2}' -f $device.name, $device.port, $device.driver)
            $targets += [pscustomobject]@{ Port = [int]$device.port; Name = [string]$device.name }
        }
    }
    else {
        Write-Host 'Список /devices недоступен, перебираю порты 9880-9898...'
        foreach ($portNumber in 9880..9898) {
            if (Test-EsclPort -PortNumber $portNumber) {
                Write-Host ('  порт {0} отвечает' -f $portNumber)
                $targets += [pscustomobject]@{ Port = $portNumber; Name = '' }
            }
        }
    }
}

if ($targets.Count -eq 0) {
    Write-Host 'Сканеры не найдены: служба не отвечает или не раздала ни одного устройства.'
    Write-Host 'Проверьте службу NAPS2.WebScan.Service и GET http://localhost:9899/devices.'
    exit 1
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Write-Host ("Результаты: {0}" -f $OutDir)

$jobs = 0
$failed = 0

# ---------------------------------------------------------------------------
# Проверка каждого сканера и каждого размера
# ---------------------------------------------------------------------------

foreach ($target in $targets) {
    $baseUrl = "http://${Address}:$($target.Port)/eSCL"

    Write-Host ''
    Write-Host ('=== Порт {0}{1} ===' -f $target.Port,
        $(if ($target.Name) { " — $($target.Name)" } else { '' }))

    $capabilities = $null
    try {
        $capabilities = Invoke-Http -Method GET -Url "$baseUrl/ScannerCapabilities" -TimeoutSec 10
    }
    catch {
        Write-Host ('  ScannerCapabilities: ошибка соединения: {0}' -f $_.Exception.Message)
        $failed++
        continue
    }

    if ($capabilities.Status -ne 200) {
        Write-Host ('  ScannerCapabilities: HTTP {0}' -f $capabilities.Status)
        $failed++
        continue
    }

    $model = Get-XmlValue -Text $capabilities.Text -Tag 'pwg:MakeAndModel'
    if ($model) {
        Write-Host ('  модель: {0}' -f $model)
    }

    foreach ($sizeName in $Size) {
        $region = $Regions[$sizeName]
        $xml = New-ScanSettingsXml -Width $region.Width -Height $region.Height `
            -Resolution $Dpi -Source $InputSource -Mode $ColorModes[$ColorMode]
        $body = [System.Text.Encoding]::UTF8.GetBytes($xml)

        Write-Host ''
        Write-Host ('--- {0}: {1}x{2} (1/300 дюйма), {3} dpi, {4}, {5} ---' -f
            $sizeName.ToUpperInvariant(), $region.Width, $region.Height, $Dpi, $ColorMode, $InputSource)

        $jobResponse = $null
        try {
            $jobResponse = Invoke-Http -Method POST -Url "$baseUrl/ScanJobs" `
                -Body $body -ContentType 'text/xml; charset=UTF-8' -TimeoutSec 30
        }
        catch {
            Write-Host ('  POST ScanJobs: ошибка соединения: {0}' -f $_.Exception.Message)
            $failed++
            continue
        }

        if ($jobResponse.Status -ne 201) {
            Write-Host ('  POST ScanJobs: HTTP {0}' -f $jobResponse.Status)
            if ($jobResponse.Text) {
                Write-Host ('  ответ: {0}' -f ($jobResponse.Text -replace '\s+', ' ').Trim())
            }
            $failed++
            continue
        }

        $location = [string]$jobResponse.Headers['Location']
        $jobId = ($location -split '/')[-1]
        $jobs++
        Write-Host ('  задание: {0}' -f $jobId)

        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        $page = 0
        $jobFailed = $false

        while ((Get-Date) -lt $deadline) {
            $document = $null
            try {
                $document = Invoke-Http -Method GET -Url "$baseUrl/ScanJobs/$jobId/NextDocument" -TimeoutSec 60
            }
            catch {
                Write-Host ('  NextDocument: ошибка соединения: {0}' -f $_.Exception.Message)
                $jobFailed = $true
                break
            }

            if ($document.Status -eq 200) {
                $page++
                $contentType = [string]$document.Headers['Content-Type']
                $extension = '.jpg'
                if ($contentType -match 'png') {
                    $extension = '.png'
                }
                elseif ($contentType -match 'pdf') {
                    $extension = '.pdf'
                }

                $fileName = '{0}-{1}-{2}-{3}-p{4}{5}' -f $target.Port, $sizeName, $Dpi, $ColorMode, $page, $extension
                $fullPath = Join-Path $OutDir $fileName
                [System.IO.File]::WriteAllBytes($fullPath, $document.Bytes)

                Write-Host ('  OK   страница {0}: {1:N1} КБ ({2}) -> {3}' -f
                    $page, ($document.Bytes.Length / 1KB), $contentType, $fileName)
                continue
            }

            if ($document.Status -eq 404) {
                if ($page -eq 0) {
                    Write-Host '  FAIL страниц нет: 404 на первую страницу'
                    $jobFailed = $true
                }
                else {
                    Write-Host ('  завершено: страниц {0}' -f $page)
                }
                break
            }

            if ($document.Status -eq 503) {
                Write-Host '  занято (503), повтор через 2 с'
                Start-Sleep -Seconds 2
                continue
            }

            Write-Host ('  FAIL HTTP {0}' -f $document.Status)
            if ($document.Status -eq 500) {
                $details = $null
                try {
                    $details = Invoke-Http -Method GET -Url "$baseUrl/ScanJobs/$jobId/ErrorDetails" -TimeoutSec 10
                }
                catch {
                    # ErrorDetails может быть недоступен — это не главное
                }
                if ($details -and $details.Text) {
                    Write-Host ('  ErrorDetails: {0}' -f ($details.Text -replace '\s+', ' ').Trim())
                }
            }
            $jobFailed = $true
            break
        }

        if (-not $jobFailed -and $page -eq 0) {
            Write-Host ('  FAIL таймаут {0} с: страниц нет' -f $TimeoutSec)
            $jobFailed = $true
        }

        if ($jobFailed) {
            $failed++
        }
    }
}

Write-Host ''
if ($failed -eq 0) {
    Write-Host ('Итог: заданий {0}, ошибок нет.' -f $jobs)
    exit 0
}

Write-Host ('Итог: заданий {0}, проблемных проверок {1}.' -f $jobs, $failed)
exit 1
