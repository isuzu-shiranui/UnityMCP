# Checks what scripts/run-editmode-tests.ps1 does to a folder before any Editor starts: a folder of
# someone's own is refused untouched, a test project made before the marker existed is taken on, and
# a link is made again without reaching what it pointed at. No Unity is needed.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/tests/run-editmode-tests.guard.ps1

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$target = Join-Path $repo 'scripts\run-editmode-tests.ps1'
$package = 'jp.shiranui-isuzu.unity-mcp'
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('editmode-guard-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$failures = 0

function Check([string] $name, [bool] $ok, [string] $detail = '') {
    if ($ok) {
        Write-Host "ok   $name"
    } else {
        Write-Host "FAIL $name $detail"
        $script:failures++
    }
}

# Runs the script in a process of its own, as a user would, with a Unity path that does not exist,
# so a run that gets past the folder checks stops there.
function Invoke-Guarded([string] $projectPath) {
    $ErrorActionPreference = 'Continue'
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $target -ProjectPath $projectPath -Unity (Join-Path $work 'no-unity.exe') 2>&1 | Out-String
    return @{ Code = $LASTEXITCODE; Output = $output }
}

function New-Junction([string] $link, [string] $pointsAt) {
    cmd /c mklink /J "`"$link`"" "`"$pointsAt`"" | Out-Null
}

New-Item -ItemType Directory -Force $work | Out-Null

try {
    $own = Join-Path $work 'own'
    $manifest = Join-Path $own 'Packages\manifest.json'
    New-Item -ItemType Directory -Force (Join-Path $own 'Packages') | Out-Null
    Set-Content -LiteralPath $manifest -Value '{"dependencies":{"com.example.kept":"1.0.0"}}'
    $before = Get-Content -Raw -LiteralPath $manifest
    $result = Invoke-Guarded $own
    Check 'a project of someone''s own is refused' ($result.Code -ne 0 -and $result.Output -match 'not a test project') $result.Output
    Check 'its manifest is left as it was' ((Get-Content -Raw -LiteralPath $manifest) -eq $before)
    Check 'no marker is written into it' (-not (Test-Path (Join-Path $own '.unity-mcp-editmode')))

    $loose = Join-Path $work 'loose'
    New-Item -ItemType Directory -Force $loose | Out-Null
    Set-Content -LiteralPath (Join-Path $loose 'notes.txt') -Value 'keep'
    $result = Invoke-Guarded $loose
    Check 'a folder with files in it is refused' ($result.Code -ne 0 -and $result.Output -match 'not a test project') $result.Output
    Check 'its files are kept' (Test-Path (Join-Path $loose 'notes.txt'))

    $decoy = Join-Path $work 'decoy'
    New-Item -ItemType Directory -Force (Join-Path $decoy $package), (Join-Path $decoy 'docs') | Out-Null
    Set-Content -LiteralPath (Join-Path $decoy "$package\keep.txt") -Value 'keep'

    $earlier = Join-Path $work 'earlier'
    New-Item -ItemType Directory -Force (Join-Path $earlier 'Packages') | Out-Null
    New-Junction (Join-Path $earlier "Packages\$package") (Join-Path $decoy $package)
    New-Junction (Join-Path $earlier 'docs') (Join-Path $decoy 'docs')
    $result = Invoke-Guarded $earlier
    Check 'a test project from before the marker gets past the folder checks' ($result.Output -match 'No Unity at') $result.Output
    Check 'it is marked as this script''s own' (Test-Path (Join-Path $earlier '.unity-mcp-editmode'))

    # The link helpers, taken out of the script and run against folders of their own.
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($target, [ref]$null, [ref]$null)
    foreach ($f in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
        if ($f.Name -in 'Remove-Link', 'Test-Link', 'Remove-ScratchProject', 'Set-Link') { . ([scriptblock]::Create($f.Extent.Text)) }
    }

    $checkout = Join-Path $work 'checkout'
    New-Item -ItemType Directory -Force (Join-Path $checkout $package), (Join-Path $checkout 'docs') | Out-Null
    Set-Content -LiteralPath (Join-Path $checkout "$package\package.json") -Value '{}'
    Set-Content -LiteralPath (Join-Path $checkout 'docs\index.md') -Value 'docs'

    $ProjectPath = Join-Path $work 'relink'
    New-Item -ItemType Directory -Force (Join-Path $ProjectPath 'Packages'), (Join-Path $ProjectPath 'docs') | Out-Null
    New-Junction (Join-Path $ProjectPath "Packages\$package") (Join-Path $decoy $package)

    Set-Link (Join-Path $ProjectPath "Packages\$package") (Join-Path $checkout $package)
    $pointsAt = [string](@((Get-Item -LiteralPath (Join-Path $ProjectPath "Packages\$package") -Force).Target) | Select-Object -First 1)
    Check 'a link to another checkout is made again' ($pointsAt.TrimEnd('\') -ieq (Join-Path $checkout $package)) $pointsAt
    Check 'what it pointed at is kept' (Test-Path (Join-Path $decoy "$package\keep.txt"))

    $refused = $false
    try { Set-Link (Join-Path $ProjectPath 'docs') (Join-Path $checkout 'docs') } catch { $refused = $true }
    Check 'a real folder where a link belongs is refused' $refused

    [System.IO.Directory]::Delete((Join-Path $ProjectPath 'docs'))
    Set-Link (Join-Path $ProjectPath 'docs') (Join-Path $checkout 'docs')
    Remove-ScratchProject
    Check 'the project is removed' (-not (Test-Path $ProjectPath))
    Check 'the package it linked to is kept' (Test-Path (Join-Path $checkout "$package\package.json"))
    Check 'the docs it linked to are kept' (Test-Path (Join-Path $checkout 'docs\index.md'))
}
finally {
    Get-ChildItem -LiteralPath $work -Recurse -Force -Attributes ReparsePoint -ErrorAction SilentlyContinue |
        ForEach-Object { cmd /c rmdir "`"$($_.FullName)`"" | Out-Null }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures -gt 0) {
    throw "$failures check(s) failed"
}

Write-Host 'all checks passed'
