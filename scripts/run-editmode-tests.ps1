# Runs the package's EditMode tests in a real Unity Editor and records the result.
#
# The release workflow verifies TypeScript and publishes; nothing automated compiles a line of
# C#. This is what makes the C# side releasable: run it, and it writes an attestation naming the
# sources it ran against. The release then refuses to publish sources that were never tested.
#
#   pwsh scripts/run-editmode-tests.ps1
#   pwsh scripts/run-editmode-tests.ps1 -Unity 'C:\Program Files\Unity\Hub\Editor\6000.0.35f1\Editor\Unity.exe'
#
# The test project is scratch: it is created on first use, reused afterwards, and holds nothing
# but a manifest and a junction back to the package in this repository, so the working tree is
# what gets compiled.

[CmdletBinding()]
param(
    [string] $Unity,
    [string] $ProjectPath = (Join-Path $env:TEMP 'unity-mcp-editmode'),
    [switch] $KeepProject
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$package = 'jp.shiranui-isuzu.unity-mcp'

# The version the attestation is made against, unless -Unity says otherwise.
#
# Pinned rather than "whichever Editor is newest on this machine": a release gate whose meaning
# depends on what happens to be installed is not a gate. The version used goes into the
# attestation, so a reader can always see what the recorded run proves.
#
# Raising this is a deliberate act. Pointing the script at 6000.5 today does not compile: the
# int instanceID APIs became obsolete-as-error there, and the package has not been migrated to
# EntityId. That is worth fixing, and it is not what this script is for.
$PinnedUnityVersion = '6000.0'

function Resolve-Unity {
    if ($Unity) {
        if (-not (Test-Path $Unity)) { throw "No Unity at '$Unity'." }
        return $Unity
    }

    $roots = @(
        'C:\Program Files\Unity\Hub\Editor',
        "$env:LOCALAPPDATA\Unity\Hub\Editor",
        '/Applications/Unity/Hub/Editor'
    ) | Where-Object { Test-Path $_ }

    foreach ($root in $roots) {
        $candidate = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name.StartsWith($PinnedUnityVersion) } |
            Sort-Object Name -Descending |
            ForEach-Object {
                @(
                    (Join-Path $_.FullName 'Editor\Unity.exe'),
                    (Join-Path $_.FullName 'Unity.app/Contents/MacOS/Unity')
                ) | Where-Object { Test-Path $_ }
            } | Select-Object -First 1

        if ($candidate) { return $candidate }
    }

    throw "No Unity $PinnedUnityVersion.x Editor found. Install one, or pass -Unity with a path."
}

$unityExe = Resolve-Unity
Write-Host "Unity:   $unityExe"
Write-Host "Project: $ProjectPath"

# ── the scratch project ───────────────────────────────────────────────────────
if (-not (Test-Path (Join-Path $ProjectPath 'Packages'))) {
    Write-Host 'Creating the test project (first run only, takes a minute)...'
    if (Test-Path $ProjectPath) { Remove-Item -Recurse -Force $ProjectPath }

    $create = Start-Process -FilePath $unityExe -Wait -PassThru -ArgumentList @(
        '-batchmode', '-nographics', '-quit', '-createProject', "`"$ProjectPath`""
    )

    if ($create.ExitCode -ne 0) { throw "createProject failed with exit code $($create.ExitCode)." }
}

# Written every run: the manifest is what pulls in Newtonsoft and the test framework, and
# `testables` is what makes Unity run tests that live inside a package at all.
$manifest = @'
{
  "dependencies": {
    "com.unity.nuget.newtonsoft-json": "3.2.1",
    "com.unity.test-framework": "1.4.6",
    "com.unity.ide.visualstudio": "2.0.22",
    "com.unity.modules.imgui": "1.0.0",
    "com.unity.modules.jsonserialize": "1.0.0",
    "com.unity.modules.uielements": "1.0.0",
    "com.unity.modules.physics": "1.0.0"
  },
  "testables": [ "jp.shiranui-isuzu.unity-mcp" ]
}
'@

# UTF-8 without a BOM: Unity's JSON parser rejects a BOM outright, and the error it reports
# names a character code rather than the file.
[System.IO.File]::WriteAllText(
    (Join-Path $ProjectPath 'Packages\manifest.json'),
    $manifest,
    (New-Object System.Text.UTF8Encoding($false)))

$link = Join-Path $ProjectPath "Packages\$package"
if (-not (Test-Path $link)) {
    $source = Join-Path $repo $package
    cmd /c mklink /J "`"$link`"" "`"$source`"" | Out-Null
    if (-not (Test-Path $link)) { throw "Could not link the package into $link." }
}

# ── run ───────────────────────────────────────────────────────────────────────
$results = Join-Path $ProjectPath 'editmode-results.xml'
$log = Join-Path $ProjectPath 'editmode.log'
Remove-Item $results -ErrorAction SilentlyContinue

Write-Host 'Running EditMode tests...'
$run = Start-Process -FilePath $unityExe -Wait -PassThru -ArgumentList @(
    '-batchmode', '-nographics',
    '-projectPath', "`"$ProjectPath`"",
    '-runTests', '-testPlatform', 'EditMode',
    '-testResults', "`"$results`"",
    '-logFile', "`"$log`""
)

if (-not (Test-Path $results)) {
    Write-Host '--- last 40 lines of the Unity log ---'
    Get-Content $log -Tail 40 -ErrorAction SilentlyContinue
    throw "Unity produced no results file (exit code $($run.ExitCode))."
}

$xml = [xml](Get-Content $results -Encoding UTF8)
$summary = $xml.'test-run'
$total = [int]$summary.total
$passed = [int]$summary.passed
$failed = [int]$summary.failed

Write-Host ''
Write-Host ("total={0} passed={1} failed={2} skipped={3}" -f $total, $passed, $failed, $summary.skipped)

if ($failed -ne 0 -or $run.ExitCode -ne 0) {
    Select-Xml -Xml $xml -XPath "//test-case[@result='Failed']" | ForEach-Object {
        Write-Host ("  FAIL " + $_.Node.fullname) -ForegroundColor Red
    }

    # No attestation on failure. A release must not be able to find one.
    throw "EditMode tests failed (exit code $($run.ExitCode))."
}

# ── record it ─────────────────────────────────────────────────────────────────
$unityVersion = (Split-Path (Split-Path $unityExe -Parent) -Leaf)
if ($unityVersion -eq 'Editor') {
    $unityVersion = (Split-Path (Split-Path (Split-Path $unityExe -Parent) -Parent) -Leaf)
}
if (-not ($unityVersion -match '^\d')) {
    $unityVersion = (Get-Item $unityExe).VersionInfo.ProductVersion
}

$hash = (& node (Join-Path $PSScriptRoot 'source-hash.js') $repo).Trim()
if (-not $hash) { throw 'Could not compute the source hash.' }

$attestation = [ordered]@{
    sourceHash   = $hash
    total        = $total
    passed       = $passed
    failed       = $failed
    # From the executable, not the results file: the Unity version is not among the properties
    # NUnit writes, and reading it from there produced an empty object rather than an error.
    unityVersion = $unityVersion
    ranAt        = (Get-Date).ToUniversalTime().ToString('o')
}

$path = Join-Path $repo 'scripts\editmode-attestation.json'
[System.IO.File]::WriteAllText(
    $path,
    (($attestation | ConvertTo-Json) + "`n").Replace("`r`n", "`n"),
    (New-Object System.Text.UTF8Encoding($false)))

Write-Host ''
Write-Host "Wrote $path" -ForegroundColor Green
Write-Host "  sourceHash $($hash.Substring(0, 16))..."
Write-Host '  Commit it with the change it covers; the release checks it before publishing.'

if (-not $KeepProject -and -not (Test-Path (Join-Path $ProjectPath 'Library'))) {
    Remove-Item -Recurse -Force $ProjectPath -ErrorAction SilentlyContinue
}
