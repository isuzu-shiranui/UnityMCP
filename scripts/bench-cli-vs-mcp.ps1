# Compares the CLI and MCP paths for driving a running Unity Editor: process-spawn overhead,
# transport overhead, and Editor-side allocation, over the same four calls.
#
#   powershell -File scripts\bench-cli-vs-mcp.ps1 -Project MyProject
#   powershell -File scripts\bench-cli-vs-mcp.ps1 -DryRun
#
# See scripts/README.md for what each path and step measures.

[CmdletBinding()]
param(
    [string] $Project = '',
    [string] $Cli = '',
    [int] $Iterations = 30,
    [int] $Warmup = 3,
    [string] $OutJson = (Join-Path $env:TEMP 'bench-cli-vs-mcp.json'),
    [switch] $DryRun
)

Add-Type -AssemblyName System.Net.Http
# The CLI writes UTF-8; PowerShell decodes child output with the console code page, which
# turns every non-ASCII character in a tool description into a different one.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$script:McpId = 0
$Steps = @(
    [PSCustomObject]@{ Name = 'play_mode_status'; Tool = 'play_mode_status'; ArgsJson = '{}' },
    [PSCustomObject]@{ Name = 'scene_browse_hierarchy'; Tool = 'scene_browse_hierarchy'; ArgsJson = '{"limit":5,"max_depth":1}' },
    [PSCustomObject]@{ Name = 'console_read_logs'; Tool = 'console_read_logs'; ArgsJson = '{"type":"error","limit":20}' }
)
$StepNames = @('play_mode_status', 'scene_browse_hierarchy', 'console_read_logs', 'tools')
$Paths = @('cli', 'mcp', 'rest')

# -- Descriptor discovery --------------------------------------------------

function Get-Descriptors {
    $dir = Join-Path $env:LOCALAPPDATA 'UnityMCP\instances'
    $found = @()
    if (-not (Test-Path -LiteralPath $dir)) { return $found }

    foreach ($file in (Get-ChildItem -LiteralPath $dir -Filter '*.json' -File -ErrorAction SilentlyContinue)) {
        $descriptor = $null
        try { $descriptor = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json }
        catch { continue }

        if (-not $descriptor.port -or -not $descriptor.token -or -not $descriptor.projectName) { continue }

        $alive = $true
        if ($descriptor.pid -and $descriptor.pid -gt 0) {
            $alive = [bool](Get-Process -Id $descriptor.pid -ErrorAction SilentlyContinue)
        }
        if ($alive) { $found += $descriptor }
    }
    return $found
}

# Matches isuzu-unity-cli's own resolution: exact project name, then a unique substring.
function Resolve-Descriptor {
    param($Descriptors, [string]$ProjectName)

    if ($Descriptors.Count -eq 0) {
        throw 'No running Unity Editor found. Open a project with the Unity MCP package installed.'
    }

    if ([string]::IsNullOrWhiteSpace($ProjectName)) {
        if ($Descriptors.Count -eq 1) { return $Descriptors[0] }
        $names = ($Descriptors | ForEach-Object { $_.projectName }) -join ', '
        throw "Several Editors are running: $names. Pass -Project <name>."
    }

    $exact = @($Descriptors | Where-Object { $_.projectName -ieq $ProjectName })
    if ($exact.Count -eq 1) { return $exact[0] }

    $partial = @($Descriptors | Where-Object { $_.projectName -ilike "*$ProjectName*" })
    if ($partial.Count -eq 1) { return $partial[0] }
    if ($partial.Count -eq 0) {
        $names = ($Descriptors | ForEach-Object { $_.projectName }) -join ', '
        throw "No running Editor matches '$ProjectName'. Running: $names"
    }
    $names = ($partial | ForEach-Object { $_.projectName }) -join ', '
    throw "'$ProjectName' matches more than one running Editor: $names. Use the full project name."
}

function Resolve-CliPath {
    param([string]$CliParam)

    if ($CliParam) { return $CliParam }

    $onPath = Get-Command 'isuzu-unity-cli' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $fallback = Join-Path $env:LOCALAPPDATA 'Programs\isuzu-unity-cli\isuzu-unity-cli.exe'
    if (Test-Path -LiteralPath $fallback) { return $fallback }

    throw "isuzu-unity-cli not found on PATH or at $fallback. Pass -Cli <path>."
}

function Get-MaskedToken {
    param([string]$Token)
    if ($Token.Length -le 8) { return '********' }
    return $Token.Substring(0, 4) + ('*' * ($Token.Length - 8)) + $Token.Substring($Token.Length - 4)
}

# -- CLI path ---------------------------------------------------------------

# PowerShell 5.1 strips a bare double quote when handing a string argument to a native exe
# (argv comes back as {limit:5} instead of {"limit":5}); CommandLineToArgvW only keeps the
# quote if it is backslash-escaped first. Every --json value must go through this before being
# passed to the CLI.
function ConvertTo-CliJsonArg {
    param([string]$Json)
    return $Json -replace '"', '\"'
}

function Invoke-CliTool {
    param([string]$CliPath, [string]$ProjectName, [string]$Tool, [string]$ArgsJson)
    $escaped = ConvertTo-CliJsonArg $ArgsJson
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $stdout = & $CliPath call $Tool --json $escaped --project $ProjectName --raw
    $sw.Stop()
    return [PSCustomObject]@{ Ms = $sw.Elapsed.TotalMilliseconds; ExitCode = $LASTEXITCODE; Stdout = ($stdout -join "`n") }
}

function Invoke-CliCatalog {
    param([string]$CliPath, [string]$ProjectName)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $stdout = & $CliPath tools --raw --project $ProjectName
    $sw.Stop()
    return [PSCustomObject]@{ Ms = $sw.Elapsed.TotalMilliseconds; ExitCode = $LASTEXITCODE; Stdout = ($stdout -join "`n") }
}

function Invoke-CliVersion {
    param([string]$CliPath)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $null = & $CliPath --version
    $sw.Stop()
    return [PSCustomObject]@{ Ms = $sw.Elapsed.TotalMilliseconds; ExitCode = $LASTEXITCODE }
}

# -- HTTP paths (MCP and REST share one HttpClient) -------------------------

function New-BenchHttpClient {
    param([string]$Token)
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.UseProxy = $false
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(30)
    $client.DefaultRequestHeaders.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $Token)
    return $client
}

function Send-BenchRequest {
    # $Body is deliberately untyped: a [string] parameter coerces a passed $null into "", which
    # then reads as "has a body" below and makes GET throw ProtocolViolationException.
    param($Client, [string]$Method, [string]$Url, $Body)
    $request = New-Object System.Net.Http.HttpRequestMessage($Method, $Url)
    if ($null -ne $Body) {
        $request.Content = New-Object System.Net.Http.StringContent($Body, [System.Text.Encoding]::UTF8, 'application/json')
    }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $response = $Client.SendAsync($request).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $sw.Stop()
    return [PSCustomObject]@{ Ms = $sw.Elapsed.TotalMilliseconds; Status = [int]$response.StatusCode; Body = $text }
}

function Invoke-RestCall {
    param($Client, [string]$Endpoint, [string]$Tool, [string]$ArgsJson)
    return Send-BenchRequest $Client 'POST' "$Endpoint/tools/$Tool" $ArgsJson
}

function Invoke-RestCatalog {
    param($Client, [string]$Endpoint)
    return Send-BenchRequest $Client 'GET' "$Endpoint/tools" $null
}

function Invoke-McpInitialize {
    param($Client, [string]$McpUrl)
    $body = '{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"bench-cli-vs-mcp","version":"1.0.0"}}}'
    Send-BenchRequest $Client 'POST' $McpUrl $body | Out-Null
}

function Invoke-McpCall {
    param($Client, [string]$McpUrl, [string]$Tool, [string]$ArgsJson)
    $script:McpId++
    $body = '{"jsonrpc":"2.0","id":' + $script:McpId + ',"method":"tools/call","params":{"name":"' + $Tool + '","arguments":' + $ArgsJson + '}}'
    return Send-BenchRequest $Client 'POST' $McpUrl $body
}

function Invoke-McpToolsList {
    param($Client, [string]$McpUrl)
    $script:McpId++
    $body = '{"jsonrpc":"2.0","id":' + $script:McpId + ',"method":"tools/list","params":{}}'
    return Send-BenchRequest $Client 'POST' $McpUrl $body
}

# GetTotalAllocatedBytes does not exist on every Mono the Editor might run on. A missing member
# fails execute_code's snippet at compile time ("'GC' does not contain a definition for
# 'GetTotalAllocatedBytes'"), not as a catchable runtime error, which would take the rest of the
# snapshot down with it. Looked up via reflection instead, so an absent method is just a null.
function Get-GcSnapshot {
    param($Client, [string]$Endpoint, [switch]$Settle)
    # -Settle collects first so the heap size that follows is a floor, not whatever the last GC left.
    $prefix = ''
    if ($Settle) { $prefix = 'System.GC.Collect(); System.GC.WaitForPendingFinalizers(); System.GC.Collect(); ' }
    $code = $prefix + 'long a = -1L; try { var m = typeof(System.GC).GetMethod("GetTotalAllocatedBytes", new System.Type[]{ typeof(bool) }); if (m != null) { a = (long)m.Invoke(null, new object[]{ false }); } } catch { a = -1L; } return new long[]{ System.GC.CollectionCount(0), System.GC.CollectionCount(1), System.GC.CollectionCount(2), System.GC.GetTotalMemory(false), a };'
    # Contains embedded string literals, so it needs real JSON escaping rather than the
    # hand-quoted "{...}" bodies used elsewhere in this script.
    $body = [PSCustomObject]@{ code = $code } | ConvertTo-Json -Compress
    $result = Send-BenchRequest $Client 'POST' "$Endpoint/tools/execute_code" $body
    if ($result.Status -ne 200) { return $null }
    $parsed = $result.Body | ConvertFrom-Json
    if ($parsed.status -ne 'success') { return $null }
    return $parsed.result.returnValue
}

# -- Equivalence (normalised JSON compare) ----------------------------------

# Sorts object keys recursively so property order (Newtonsoft vs System.Text.Json) never
# causes a false mismatch. Array order is preserved because array order is real data.
function ConvertTo-CanonicalJson {
    param($Node)
    if ($null -eq $Node) { return 'null' }
    if ($Node -is [System.Management.Automation.PSCustomObject]) {
        $parts = @($Node.PSObject.Properties | Sort-Object Name | ForEach-Object {
            (ConvertTo-Json $_.Name -Compress) + ':' + (ConvertTo-CanonicalJson $_.Value)
        })
        return '{' + ($parts -join ',') + '}'
    }
    if ($Node -is [string]) { return ConvertTo-Json $Node -Compress }
    if ($Node -is [bool]) { if ($Node) { return 'true' } else { return 'false' } }
    if ($Node -is [System.Collections.IEnumerable]) {
        $parts = @($Node | ForEach-Object { ConvertTo-CanonicalJson $_ })
        return '[' + ($parts -join ',') + ']'
    }
    return "$Node"
}

# The MCP structuredContent carries pagination fields (truncated/next) verbatim; the REST/CLI
# envelope hoists those two keys out of `result` onto the envelope itself. Stripped here so the
# comparison is about the data, not where each transport happens to put two bookkeeping keys.
function Remove-PaginationKeys {
    param($Node)
    if ($Node -isnot [System.Management.Automation.PSCustomObject]) { return $Node }
    $clean = [ordered]@{}
    foreach ($p in $Node.PSObject.Properties) {
        if ($p.Name -eq 'truncated' -or $p.Name -eq 'next') { continue }
        $clean[$p.Name] = $p.Value
    }
    return [PSCustomObject]$clean
}

function Test-Equivalent {
    param($A, $B, [string]$Label)
    $canonA = ConvertTo-CanonicalJson (Remove-PaginationKeys $A)
    $canonB = ConvertTo-CanonicalJson (Remove-PaginationKeys $B)
    if ($canonA -ne $canonB) {
        Write-Host "EQUIVALENCE FAILED: $Label" -ForegroundColor Red
        Write-Host "  A: $canonA"
        Write-Host "  B: $canonB"
        return $false
    }
    return $true
}

# -- Stats ----------------------------------------------------------------

function Get-Stats {
    param([double[]]$Values)
    $n = $Values.Count
    if ($n -eq 0) { return [PSCustomObject]@{ Mean = 0; P50 = 0; P95 = 0; Min = 0; N = 0 } }
    $sorted = $Values | Sort-Object
    $mean = ($sorted | Measure-Object -Average).Average
    $p50 = $sorted[[Math]::Min($n - 1, [int][Math]::Floor(0.50 * $n))]
    $p95 = $sorted[[Math]::Min($n - 1, [int][Math]::Floor(0.95 * $n))]
    return [PSCustomObject]@{
        Mean = [Math]::Round($mean, 2); P50 = [Math]::Round($p50, 2)
        P95  = [Math]::Round($p95, 2); Min = [Math]::Round($sorted[0], 2); N = $n
    }
}

# -- Main -----------------------------------------------------------------

$exitCode = 0
try {
    $cliPath = $null
    $cliError = $null
    try { $cliPath = Resolve-CliPath $Cli }
    catch { $cliError = $_.Exception.Message }

    $descriptors = Get-Descriptors
    $descriptor = $null
    $resolveError = $null
    try { $descriptor = Resolve-Descriptor $descriptors $Project }
    catch { $resolveError = $_.Exception.Message }

    if ($DryRun) {
        Write-Host 'DRY RUN -- no request will be sent.'
        if ($cliPath) { Write-Host "CLI path: $cliPath" } else { Write-Host "CLI not found: $cliError" }
        Write-Host "Iterations: $Iterations  Warmup: $Warmup  OutJson: $OutJson"
        Write-Host "Steps: $($StepNames -join ', ')"
        Write-Host "Paths: $($Paths -join ', ')"
        if ($descriptor) {
            Write-Host "Project: $($descriptor.projectName)"
            Write-Host "Endpoint: $($descriptor.endpoint)"
            Write-Host "McpUrl: $($descriptor.mcpUrl)"
            Write-Host "Token: $(Get-MaskedToken $descriptor.token)"
        } else {
            Write-Host "No Editor resolved: $resolveError"
        }
        exit 0
    }

    if ($cliError) { throw $cliError }
    if ($resolveError) { throw $resolveError }

    $endpoint = $descriptor.endpoint
    $mcpUrl = $descriptor.mcpUrl
    $projectName = $descriptor.projectName
    Write-Host "Target: $projectName ($endpoint)"

    $client = New-BenchHttpClient $descriptor.token

    $reachable = $false
    try { $reachable = ((Send-BenchRequest $client 'GET' "$endpoint/health" $null).Status -eq 200) } catch { $reachable = $false }
    if (-not $reachable) { throw "Editor at $endpoint is unreachable." }

    # -- Equivalence pass (once per step, before any timing) --
    $equivalenceOk = $true
    foreach ($step in $Steps) {
        $restRes = Invoke-RestCall $client $endpoint $step.Tool $step.ArgsJson
        $mcpRes = Invoke-McpCall $client $mcpUrl $step.Tool $step.ArgsJson
        $cliRes = Invoke-CliTool $cliPath $projectName $step.Tool $step.ArgsJson

        $restObj = $restRes.Body | ConvertFrom-Json
        $mcpObj = $mcpRes.Body | ConvertFrom-Json
        $cliObj = $cliRes.Stdout | ConvertFrom-Json

        if ($restObj.status -ne 'success') { throw "REST call to $($step.Tool) failed: $($restObj.error.message)" }
        if ($mcpObj.error) { throw "MCP call to $($step.Tool) failed: $($mcpObj.error.message)" }
        if ($cliObj.status -ne 'success') { throw "CLI call to $($step.Tool) failed: exit $($cliRes.ExitCode)" }

        if (-not (Test-Equivalent $restObj.result $mcpObj.result.structuredContent "$($step.Name): REST result vs MCP structuredContent")) { $equivalenceOk = $false }
        if (-not (Test-Equivalent $restObj.result $cliObj.result "$($step.Name): REST result vs CLI stdout")) { $equivalenceOk = $false }
    }

    $restCatalog = Invoke-RestCatalog $client $endpoint
    $cliCatalog = Invoke-CliCatalog $cliPath $projectName
    $mcpCatalog = Invoke-McpToolsList $client $mcpUrl
    $restCatalogObj = ($restCatalog.Body | ConvertFrom-Json).result
    $cliCatalogObj = ($cliCatalog.Stdout | ConvertFrom-Json).result
    $mcpCatalogObj = ($mcpCatalog.Body | ConvertFrom-Json).result

    if (-not (Test-Equivalent $restCatalogObj $cliCatalogObj 'tools: REST catalog vs CLI catalog')) { $equivalenceOk = $false }
    $restNames = ($restCatalogObj.tools | ForEach-Object { $_.name } | Sort-Object) -join ','
    $mcpNames = ($mcpCatalogObj.tools | ForEach-Object { $_.name } | Sort-Object) -join ','
    if ($restNames -ne $mcpNames) {
        Write-Host 'EQUIVALENCE FAILED: tools: REST catalog names vs MCP tools/list names' -ForegroundColor Red
        $equivalenceOk = $false
    }

    if (-not $equivalenceOk) { throw 'One or more equivalence checks failed. See EQUIVALENCE FAILED lines above.' }
    Write-Host 'Equivalence checks passed.'

    # -- Timed loops --
    # One scriptblock per path per operation, keyed by path name, so the loop body below does
    # not repeat itself three times over.
    $callFn = @{
        cli  = { param($tool, $argsJson) Invoke-CliTool $cliPath $projectName $tool $argsJson }
        mcp  = { param($tool, $argsJson) Invoke-McpCall $client $mcpUrl $tool $argsJson }
        rest = { param($tool, $argsJson) Invoke-RestCall $client $endpoint $tool $argsJson }
    }
    $catalogFn = @{
        cli  = { Invoke-CliCatalog $cliPath $projectName }
        mcp  = { Invoke-McpToolsList $client $mcpUrl }
        rest = { Invoke-RestCatalog $client $endpoint }
    }
    $isSuccessFn = @{
        cli  = { param($r) $r.ExitCode -eq 0 }
        mcp  = { param($r) $r.Status -eq 200 }
        rest = { param($r) $r.Status -eq 200 }
    }

    $results = @{}
    $gc = @{}
    foreach ($path in $Paths) { $results[$path] = @{}; foreach ($s in $StepNames) { $results[$path][$s] = New-Object System.Collections.Generic.List[double] } }

    Invoke-McpInitialize $client $mcpUrl

    foreach ($path in $Paths) {
        for ($i = 0; $i -lt $Warmup; $i++) {
            foreach ($step in $Steps) { & $callFn[$path] $step.Tool $step.ArgsJson | Out-Null }
            & $catalogFn[$path] | Out-Null
        }

        $before = Get-GcSnapshot $client $endpoint -Settle
        for ($i = 0; $i -lt $Iterations; $i++) {
            foreach ($step in $Steps) {
                $r = & $callFn[$path] $step.Tool $step.ArgsJson
                if (& $isSuccessFn[$path] $r) { $results[$path][$step.Name].Add($r.Ms) }
            }
            $r = & $catalogFn[$path]
            if (& $isSuccessFn[$path] $r) { $results[$path]['tools'].Add($r.Ms) }
        }
        $after = Get-GcSnapshot $client $endpoint

        $totalRequests = $Iterations * $StepNames.Count
        $entry = [PSCustomObject]@{ Gen0Per100 = 0; Gen1Per100 = 0; Gen2Per100 = 0; AllocatedBytesPer100 = $null; HeapGrowthBytesPer100 = $null; GcRanDuringLoop = $false }
        if ($before -and $after) {
            $entry.Gen0Per100 = [Math]::Round((($after[0] - $before[0]) / $totalRequests) * 100, 2)
            # Heap growth without a collection is the allocation of the loop; with one it is only a floor.
            $entry.HeapGrowthBytesPer100 = [Math]::Round((($after[3] - $before[3]) / $totalRequests) * 100, 0)
            $entry.GcRanDuringLoop = ($after[0] -ne $before[0])
            $entry.Gen1Per100 = [Math]::Round((($after[1] - $before[1]) / $totalRequests) * 100, 2)
            $entry.Gen2Per100 = [Math]::Round((($after[2] - $before[2]) / $totalRequests) * 100, 2)
            if ($before[4] -ge 0 -and $after[4] -ge 0) {
                $entry.AllocatedBytesPer100 = [Math]::Round((($after[4] - $before[4]) / $totalRequests) * 100, 0)
            }
        }
        $gc[$path] = $entry
    }

    # Pure process-start baseline: no tool call, no Editor round trip.
    $baselineSamples = New-Object System.Collections.Generic.List[double]
    for ($i = 0; $i -lt $Warmup; $i++) { Invoke-CliVersion $cliPath | Out-Null }
    for ($i = 0; $i -lt $Iterations; $i++) {
        $r = Invoke-CliVersion $cliPath
        if ($r.ExitCode -eq 0) { $baselineSamples.Add($r.Ms) }
    }
    $baselineStats = Get-Stats $baselineSamples.ToArray()

    # -- Report --
    $fmt = "{0,-6} {1,-24} {2,10} {3,10} {4,10} {5,10} {6,6}"
    Write-Host ''
    Write-Host ($fmt -f 'Path', 'Step', 'Mean(ms)', 'P50(ms)', 'P95(ms)', 'Min(ms)', 'N')
    $statsTable = @{}
    foreach ($path in $Paths) {
        $statsTable[$path] = @{}
        foreach ($step in $StepNames) {
            $stats = Get-Stats $results[$path][$step].ToArray()
            $statsTable[$path][$step] = $stats
            Write-Host ($fmt -f $path, $step, $stats.Mean, $stats.P50, $stats.P95, $stats.Min, $stats.N)
        }
    }
    Write-Host ''
    Write-Host ("CLI process-start baseline: mean {0} ms, p50 {1} ms, p95 {2} ms, min {3} ms, n {4}" -f $baselineStats.Mean, $baselineStats.P50, $baselineStats.P95, $baselineStats.Min, $baselineStats.N)
    Write-Host ''
    Write-Host 'Gen0 collections per 100 requests (allocation proxy):'
    foreach ($path in $Paths) {
        Write-Host ("  {0,-6} gen0={1} heapGrowthBytes/100={2}{3} allocBytes/100={4}" -f $path, $gc[$path].Gen0Per100, $gc[$path].HeapGrowthBytesPer100, $(if ($gc[$path].GcRanDuringLoop) { " (GC ran; floor)" } else { "" }), $gc[$path].AllocatedBytesPer100)
    }

    $rawSamples = @{}
    foreach ($path in $Paths) { $rawSamples[$path] = @{}; foreach ($step in $StepNames) { $rawSamples[$path][$step] = $results[$path][$step].ToArray() } }

    $summary = [PSCustomObject]@{
        generatedAt = (Get-Date).ToUniversalTime().ToString('o')
        project     = $projectName
        endpoint    = $endpoint
        iterations  = $Iterations
        warmup      = $Warmup
        baseline    = [PSCustomObject]@{ processStartMs = $baselineStats }
        results     = $statsTable
        gc          = $gc
        equivalence = [PSCustomObject]@{ passed = $true }
        rawSamplesMs = $rawSamples
    }
    # Out-File -Encoding utf8 adds a BOM in Windows PowerShell 5.1, which some JSON parsers
    # (e.g. Python's json module) reject outright. Written without one instead.
    $json = $summary | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText($OutJson, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ''
    Write-Host "Wrote raw samples and summary to $OutJson"
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
}

exit $exitCode
