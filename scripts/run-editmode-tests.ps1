# Runs the package's EditMode tests in a real Unity Editor and records the result.
#
# No runner has a Unity licence, so nothing automated compiles the Editor assemblies. This is
# what makes them releasable: run it, and it writes an attestation naming the sources it ran
# against. CI and the release then refuse sources that no recorded run covers.
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

# The Editor generation used when -Unity is not given.
#
# A fixed default rather than "whichever Editor is newest on this machine": a release gate whose
# meaning depends on what happens to be installed is not a gate. -Unity runs the same tests on
# another Editor; the version used goes into the attestation either way, so a reader can always
# see what the recorded run proves.
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

$unityVersion = (Split-Path (Split-Path $unityExe -Parent) -Leaf)
if ($unityVersion -eq 'Editor') {
    $unityVersion = (Split-Path (Split-Path (Split-Path $unityExe -Parent) -Parent) -Leaf)
}
if (-not ($unityVersion -match '^\d')) {
    $unityVersion = (Get-Item $unityExe).VersionInfo.ProductVersion
}

# Timeline and Recorder come from the Editor's own bundled package set: a version from another
# Editor generation fails to compile inside that package (2022.3 lacks a ShowSelector overload
# Recorder 5.0 uses; 6.5 treats the GetInstanceID call in Timeline 1.8.7 as an error), and the
# resulting log looks like a defect in this package.
#
# Windows and Linux keep the set under Data\ beside the executable; macOS keeps it under
# Unity.app/Contents/Resources, two levels above Contents/MacOS/Unity.
$editorDir = Split-Path $unityExe
$bundled = @(
    (Join-Path $editorDir 'Data\Resources\PackageManager\Editor'),
    (Join-Path (Split-Path $editorDir) 'Resources\PackageManager\Editor')
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $bundled) {
    Write-Warning "No bundled package set found beside '$unityExe'; falling back to timeline 1.8.7 and recorder 5.0.0, which compile only on 6000.0."
}

function Resolve-Bundled([string] $name, [string] $fallback) {
    if (-not $bundled) { return $fallback }
    $hit = Get-ChildItem $bundled -Filter "$name-*.tgz" -ErrorAction SilentlyContinue |
        ForEach-Object { $_.BaseName.Substring($name.Length + 1) } |
        Sort-Object { [version](($_ -split '-')[0]) } -Descending |
        Select-Object -First 1
    if ($hit) { return $hit }
    Write-Warning "No $name-*.tgz under '$bundled'; falling back to $name $fallback."
    return $fallback
}
$timelineVersion = Resolve-Bundled 'com.unity.timeline' '1.8.7'
$recorderVersion = Resolve-Bundled 'com.unity.recorder' '5.0.0'

if ($unityVersion -match '^(\d+)\.') {
    $editorMajor = [int]$Matches[1]
} else {
    Write-Warning "Could not read an Editor version from '$unityExe' (got '$unityVersion'); assuming the 6000 generation for the test framework."
    $editorMajor = 6000
}
$testFrameworkVersion = if ($editorMajor -lt 6000) { '1.1.33' } else { '1.4.6' }

# Written every run: the manifest is what pulls in Newtonsoft and the test framework, and
# `testables` is what makes Unity run tests that live inside a package at all.
$manifest = @"
{
  "dependencies": {
    "com.unity.nuget.newtonsoft-json": "3.2.1",
    "com.unity.test-framework": "$testFrameworkVersion",
    "com.unity.ide.visualstudio": "2.0.22",
    "com.unity.modules.imgui": "1.0.0",
    "com.unity.modules.jsonserialize": "1.0.0",
    "com.unity.modules.uielements": "1.0.0",
    "com.unity.modules.physics": "1.0.0",
    "com.unity.modules.animation": "1.0.0",
    "com.unity.modules.director": "1.0.0",
    "com.unity.timeline": "$timelineVersion",
    "com.unity.recorder": "$recorderVersion"
  },
  "testables": [ "jp.shiranui-isuzu.unity-mcp" ]
}
"@

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
# Taken before the Editor starts and compared with the one taken after. An edit landing while
# the tests run would otherwise be recorded as covered by a run that never compiled it.
$hashBefore = (& dotnet run (Join-Path $PSScriptRoot 'source-hash.cs') -- $repo).Trim()
if (-not $hashBefore) { throw 'Could not compute the source hash.' }

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
$skipped = [int]$summary.skipped

Write-Host ''
Write-Host ("total={0} passed={1} failed={2} skipped={3}" -f $total, $passed, $failed, $skipped)

if ($failed -ne 0 -or $run.ExitCode -ne 0) {
    Select-Xml -Xml $xml -XPath "//test-case[@result='Failed']" | ForEach-Object {
        Write-Host ("  FAIL " + $_.Node.fullname) -ForegroundColor Red
    }

    # No attestation on failure. A release must not be able to find one.
    throw "EditMode tests failed (exit code $($run.ExitCode))."
}

# ── record it ─────────────────────────────────────────────────────────────────
# $repo is absolute on purpose: `dotnet run` sets the working directory to the folder holding
# the .cs file, so a relative root would be resolved against scripts/.
$hash = (& dotnet run (Join-Path $PSScriptRoot 'source-hash.cs') -- $repo).Trim()
if (-not $hash) { throw 'Could not compute the source hash.' }

if ($hash -ne $hashBefore) {
    throw ("The Editor sources changed while the tests were running " +
           "($($hashBefore.Substring(0, 16))... -> $($hash.Substring(0, 16))...). " +
           'No attestation written: the run did not compile the sources now on disk. Re-run it.')
}

$attestation = [ordered]@{
    sourceHash   = $hash
    total        = $total
    passed       = $passed
    failed       = $failed
    # The gate requires passed + failed + skipped == total: NUnit counts inconclusive and
    # not-run cases in total without listing them under failed.
    skipped      = $skipped
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
