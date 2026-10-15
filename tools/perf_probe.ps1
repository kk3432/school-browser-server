# Performance probe + budget gate for the campus-browser server (ASCII-only source on purpose:
# Windows PowerShell reads non-BOM scripts as ANSI, which breaks literal Chinese).
#
# What it does:
#   1) publish the server (self-contained win-x64) into .buildtools/perftest
#   2) boot it on a fresh data/ dir with CAMPUS_PERF_SEED=1, run setup + login
#   3) seed 200 devices + a full day of 5s online samples (17,280 points) and a config with
#      a few built-in HTML pages, so the read paths see realistic data instead of an empty DB
#   4) warm up, then measure per-route latency / response bytes for the hot endpoints,
#      including the incremental overview mode (?since=) used by the 1s dashboard refresh
#   5) read GET /api/admin/diag/perf (route stats + SQLite write counters) and compare against
#      the budgets carried in that response; exit 1 when any route fails
#
# Usage:
#   pwsh -File tools/perf_probe.ps1 [-Port 18081] [-Rounds 30] [-SkipBuild]
#
param(
    [int]$Port = 18081,
    [int]$Rounds = 30,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot          # .../school-browser-server
$ws = Split-Path -Parent $repo                    # .../campus-browser-source
$bt = Join-Path $ws '.buildtools'
$dotnet = Join-Path $bt 'dotnet\dotnet.exe'
$out = Join-Path $bt 'perftest'
$base = "http://127.0.0.1:$Port"
$fail = 0

# Sandbox-safe dotnet environment: the DSH file sandbox allows writes only inside the
# workspace, but dotnet writes its CLI state to $HOME\.dotnet and NuGet to $HOME\.nuget,
# which fails with "Access to the path 'C:\Users\Home\.dotnet' is denied".
# Redirect both into .buildtools so publish/restore work without widening permissions.
$env:DOTNET_CLI_HOME = Join-Path $bt 'dotnet-home'
$env:NUGET_PACKAGES = Join-Path $bt 'nuget'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if (-not $SkipBuild) {
    Write-Output "== publish -> $out =="
    & $dotnet publish (Join-Path $repo 'CampusBrowser.Server.csproj') -c Release -r win-x64 --self-contained true -o $out 2>&1 |
        Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }
}

Remove-Item (Join-Path $out 'data') -Recurse -Force -ErrorAction SilentlyContinue
$env:Logging__EventLog__LogLevel__Default = 'None'
# Enables POST /api/admin/diag/seed (returns 404 without it: production never sets this).
$env:CAMPUS_PERF_SEED = '1'
$proc = Start-Process -FilePath (Join-Path $out 'CampusBrowser.Server.exe') `
    -ArgumentList '--urls', $base -WorkingDirectory $out -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $out 'out.log') -RedirectStandardError (Join-Path $out 'err.log')

Add-Type -AssemblyName System.Net.Http
# Automatic decompression so the measured bytes are the payload size; the on-wire (compressed)
# size is what /api/admin/diag/perf reports, because ResponseCompression runs before the
# counting middleware.
$handler = New-Object System.Net.Http.HttpClientHandler
$enc = [System.Net.DecompressionMethods]::GZip -bor [System.Net.DecompressionMethods]::Deflate
foreach ($name in [enum]::GetNames([System.Net.DecompressionMethods])) {
    if ($name -eq 'Brotli') { $enc = $enc -bor [System.Net.DecompressionMethods]::Brotli }
}
$handler.AutomaticDecompression = $enc
$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds(120)
$appUa = @{ 'User-Agent' = 'OkHttp/4.12.0' }

function New-Request([string]$method, [string]$path, $headers, $json) {
    $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($method), "$base$path")
    if ($headers) { foreach ($k in $headers.Keys) { [void]$req.Headers.TryAddWithoutValidation($k, $headers[$k]) } }
    foreach ($k in $appUa.Keys) { [void]$req.Headers.TryAddWithoutValidation($k, $appUa[$k]) }
    if ($json) { $req.Content = New-Object System.Net.Http.StringContent($json, [System.Text.Encoding]::UTF8, 'application/json') }
    return $req
}

function Invoke-Json([string]$method, [string]$path, $headers, $json) {
    $req = New-Request $method $path $headers $json
    $resp = $client.SendAsync($req).GetAwaiter().GetResult()
    $text = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    return [pscustomobject]@{ Status = [int]$resp.StatusCode; Text = $text }
}

function Measure-Call([string]$method, [string]$path, $headers) {
    $req = New-Request $method $path $headers $null
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $resp = $client.SendAsync($req).GetAwaiter().GetResult()
    $bytes = $resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    $sw.Stop()
    return [pscustomobject]@{ Ms = $sw.Elapsed.TotalMilliseconds; Bytes = $bytes.Length; Status = [int]$resp.StatusCode }
}

function Wait-Ready {
    for ($i = 1; $i -le 40; $i++) {
        Start-Sleep -Milliseconds 500
        try { $r = Invoke-Json 'GET' '/api/setup/status' $null $null; if ($r.Status -eq 200) { return $true } } catch { }
    }
    return $false
}

# A config with 3 built-in HTML pages (~120 KB total) so /api/v1/config and the html-pages
# endpoints carry the body volume a real school config would have.
function Get-SetupJson {
    $page = '<div class="notice">' + ('x' * 40000) + '</div>'
    $pages = @(
        @{ slug = 'notice-2026'; title = 'Notice'; html = $page },
        @{ slug = 'timetable'; title = 'Timetable'; html = $page },
        @{ slug = 'contacts'; title = 'Contacts'; html = $page }
    )
    $obj = [ordered]@{
        username = 'admin'; password = 'test123456'; title = 'Perf Portal'
        homeUrl = 'http://example.com/'; fallbackUrl = 'http://example.com/offline'
        mode = 'whitelist'; rules = 'example.com'; bookmarks = @()
        updateIntervalSeconds = 300; kiosk = $false; hiddenEntryEnabled = $true
        blockScreenshot = $true; requireStartupPhoto = $false; allowedApps = @()
        adminPin = '123456'; htmlPages = $pages
    }
    return ($obj | ConvertTo-Json -Depth 6 -Compress)
}

try {
    if (-not (Wait-Ready)) { throw 'server not ready' }

    Write-Output '== setup + login =='
    $r = Invoke-Json 'POST' '/api/setup' $null (Get-SetupJson)
    if ($r.Status -ne 200) { throw "setup failed: $($r.Status) $($r.Text)" }
    $r = Invoke-Json 'POST' '/api/login' $null '{"username":"admin","password":"test123456"}'
    $token = ($r.Text | ConvertFrom-Json).token
    $H = @{ Authorization = "Bearer $token" }
    [void](Invoke-Json 'POST' '/api/v1/devices/register' $null '{"deviceId":"perf-device","name":"Perf","appVersion":"0.8.2","configVersion":"1.0.0","ipAddress":"10.0.0.9"}')

    Write-Output '== seed (200 devices + full day of 5s samples) =='
    $seed = Invoke-Json 'POST' '/api/admin/diag/seed?devices=200&samples=17280' $H $null
    if ($seed.Status -ne 200) { throw "seed failed: $($seed.Status) $($seed.Text)" }
    Write-Output ("seed -> {0}" -f $seed.Text)

    $targets = @(
        @{ Name = 'GET /api/admin/stats/overview'; Path = '/api/admin/stats/overview'; Method = 'GET' },
        @{ Name = 'GET /api/admin/stats/overview (delta)'; Method = 'GET'; PathFn = { param($i) "/api/admin/stats/overview?since=$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())" } },
        @{ Name = 'GET /api/admin/devices'; Path = '/api/admin/devices'; Method = 'GET' },
        @{ Name = 'GET /api/admin/config/versions'; Path = '/api/admin/config/versions'; Method = 'GET' },
        @{ Name = 'GET /api/admin/html-pages'; Path = '/api/admin/html-pages'; Method = 'GET' },
        @{ Name = 'GET /api/v1/config'; Path = '/api/v1/config?device_id=perf-device'; Method = 'GET' },
        @{ Name = 'POST /api/v1/devices/heartbeat'; Path = '/api/v1/devices/heartbeat'; Method = 'POST' }
    )

    function Invoke-Target($t, $i, $H) {
        $path = if ($t.PathFn) { & $t.PathFn $i } else { $t.Path }
        if ($t.Method -eq 'POST') {
            $body = '{"deviceId":"perf-device","name":"Perf","appVersion":"0.8.2","configVersion":"1.0.0","ipAddress":"10.0.0.9"}'
            $req = New-Request 'POST' $path $H $body
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $resp = $client.SendAsync($req).GetAwaiter().GetResult()
            $b = $resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            $sw.Stop()
            return [pscustomobject]@{ Ms = $sw.Elapsed.TotalMilliseconds; Bytes = $b.Length; Status = [int]$resp.StatusCode }
        }
        return Measure-Call 'GET' $path $H
    }

    Write-Output '== warmup =='
    foreach ($t in $targets) { 3 | ForEach-Object { [void](Invoke-Target $t 0 $H) } }

    # reset server-side counters so the measured window is clean
    $diag0 = Invoke-Json 'GET' '/api/admin/diag/perf?reset=true' $H $null
    $hasDiag = $diag0.Status -eq 200

    $samples = @{}
    foreach ($t in $targets) { $samples[$t.Name] = New-Object System.Collections.ArrayList }

    Write-Output "== measuring $Rounds rounds =="
    foreach ($i in 1..$Rounds) {
        foreach ($t in $targets) {
            [void]$samples[$t.Name].Add((Invoke-Target $t $i $H))
        }
    }

    Write-Output ''
    Write-Output ('{0,-40} {1,6} {2,9} {3,9} {4,10}' -f 'route', 'count', 'avg_ms', 'max_ms', 'avg_bytes')
    foreach ($t in $targets) {
        $s = $samples[$t.Name]
        $avg = ($s | Measure-Object -Property Ms -Average).Average
        $max = ($s | Measure-Object -Property Ms -Maximum).Maximum
        $avgB = ($s | Measure-Object -Property Bytes -Average).Average
        Write-Output ('{0,-40} {1,6} {2,9:N2} {3,9:N2} {4,10:N0}' -f $t.Name, $s.Count, $avg, $max, $avgB)
    }

    if ($hasDiag) {
        $diag = (Invoke-Json 'GET' '/api/admin/diag/perf' $H $null).Text | ConvertFrom-Json
        Write-Output ''
        Write-Output '== server diag (/api/admin/diag/perf) =='
        Write-Output ("uptime={0}s auditQueue={1}" -f $diag.uptimeSeconds, $diag.auditQueue)
        Write-Output ("db: connOpens={0} transactions={1} writes={2} tokenLookups={3} tokenCacheHits={4} tokenRenewWrites={5} logBatches={6}" -f `
                $diag.db.connOpens, $diag.db.transactions, $diag.db.writes, $diag.db.tokenLookups, $diag.db.tokenCacheHits, $diag.db.tokenRenewWrites, $diag.db.logBatches)
        Write-Output ("cache: overview={0}/{1} hit/miss  config={2}/{3} hit/miss" -f `
                $diag.db.overviewCacheHits, $diag.db.overviewCacheMisses, $diag.db.configCacheHits, $diag.db.configCacheMisses)
        foreach ($rt in $diag.routes) {
            $verdict = if ($rt.budgetBytes -gt 0) { $(if ($rt.pass) { 'PASS' } else { 'FAIL' }) } else { '-' }
            Write-Output ('  {0,-40} n={1,-5} avgMs={2,-8:N2} maxMs={3,-8:N2} avgBytes={4,-9:N0} maxBytes={5,-9:N0} budget(avgMs<={6},bytes<={7}) {8}' -f `
                    $rt.route, $rt.count, $rt.avgMs, $rt.maxMs, $rt.avgBytes, $rt.maxBytes, $rt.budgetAvgMs, $rt.budgetBytes, $verdict)
            if ($rt.budgetBytes -gt 0 -and -not $rt.pass) { $fail++ }
        }
    }
    else {
        Write-Output ''
        Write-Output 'NOTE: /api/admin/diag/perf not available (pre-instrumentation build) - timings only, no budget gate.'
    }

    $dbPath = Join-Path $out 'data\campus.db'
    if (Test-Path $dbPath) {
        $dbSize = (Get-Item $dbPath).Length
        $walSize = if (Test-Path "$dbPath-wal") { (Get-Item "$dbPath-wal").Length } else { 0 }
        Write-Output ''
        Write-Output ("db file: campus.db={0:N0} B  campus.db-wal={1:N0} B" -f $dbSize, $walSize)
    }

    Write-Output ''
    if ($fail -gt 0) { Write-Output "BUDGET FAIL: $fail route(s) over budget"; exit 1 }
    Write-Output 'PERF PROBE OK'
}
finally {
    $proc.Refresh()
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force; Start-Sleep -Milliseconds 500 }
    Write-Output '--- server log tail ---'
    Get-Content (Join-Path $out 'out.log') -Tail 5 -ErrorAction SilentlyContinue
    $client.Dispose()
}
