#requires -version 5.1
<#
.SYNOPSIS
  Installs isuzu-unity-cli from GitHub Releases (isuzu-shiranui/UnityMCP).

.DESCRIPTION
  Download this file and run it, or pipe it directly:
    irm https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/install.ps1 | iex

  Piping through iex leaves no script file behind, so command-line parameters
  cannot be bound. Use the environment variables below in that case; direct
  invocation (powershell -File install.ps1 -Version v4.0.0) also works.

.PARAMETER Version
  Release tag to install, e.g. "v4.0.0" or "4.0.0". Defaults to "latest".
  Falls back to $env:ISUZU_UNITY_CLI_VERSION when not passed.

.PARAMETER InstallDir
  Directory to install into. Defaults to "%LOCALAPPDATA%\Programs\isuzu-unity-cli".
  Falls back to $env:ISUZU_UNITY_CLI_DIR when not passed.
#>
param(
    [string]$Version = "",
    [string]$InstallDir = ""
)

# The body runs inside a scriptblock so that $ErrorActionPreference belongs to a scope of its own.
# The documented install path is `irm ... | iex`, which runs in the caller's session, and setting
# the preference at the top of the file would leave every later non-terminating error in that
# terminal fatal, long after the install finished.
#
# Failures throw for the same reason: `exit` would close the session the one-liner was pasted into.
# Run as a file, an unhandled throw still leaves the process with exit code 1.
& {
    param([string]$Version, [string]$InstallDir)

    $ErrorActionPreference = "Stop"

    # [Environment]::GetEnvironmentVariable("Path", "User") expands %VAR% references before
    # returning them, and SetEnvironmentVariable writes what it is handed back as REG_SZ. Together
    # they rewrite a PATH holding %JAVA_HOME%\bin to whatever that variable happened to be at
    # install time, and it stops tracking the variable from then on. The value is read unexpanded
    # and written back under its own type instead.
    function Add-DirectoryToUserPath {
        param([string]$Directory)

        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("Environment", $true)
        if (-not $key) {
            throw "Could not open HKCU\Environment for writing, so $Directory was not added to your PATH."
        }

        try {
            $raw = ""
            $kind = [Microsoft.Win32.RegistryValueKind]::ExpandString
            if ($key.GetValueNames() -contains "Path") {
                $raw = [string]$key.GetValue(
                    "Path", "", [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                # A PATH already stored as a plain string stays one. Everything else, a missing
                # value included, is written as REG_EXPAND_SZ, which is the type Windows gives it.
                if ($key.GetValueKind("Path") -eq [Microsoft.Win32.RegistryValueKind]::String) {
                    $kind = [Microsoft.Win32.RegistryValueKind]::String
                }
            }

            $entries = $raw -split ";" | Where-Object { $_ -ne "" }
            if ($entries | Where-Object { $_.TrimEnd("\") -ieq $Directory.TrimEnd("\") }) {
                return $false
            }

            $updated = if ($raw -and -not $raw.EndsWith(";")) { "$raw;$Directory" } else { "$raw$Directory" }
            $key.SetValue("Path", $updated, $kind)
        } finally {
            $key.Close()
        }

        return $true
    }

    # Writing the registry directly skips the WM_SETTINGCHANGE that SetEnvironmentVariable sends,
    # and without it Explorer keeps handing its stale environment to everything it launches until
    # the next sign-in, which makes "open a new terminal" untrue.
    function Send-EnvironmentChange {
        if (-not ("IsuzuUnityCli.NativeMethods" -as [type])) {
            $signature = @'
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
public static extern IntPtr SendMessageTimeout(
    IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam,
    uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
'@
            Add-Type -MemberDefinition $signature -Name "NativeMethods" -Namespace "IsuzuUnityCli" | Out-Null
        }

        # HWND_BROADCAST, WM_SETTINGCHANGE, SMTO_ABORTIFHUNG, one second per window.
        $ignored = [UIntPtr]::Zero
        [void][IsuzuUnityCli.NativeMethods]::SendMessageTimeout(
            [IntPtr]0xffff, 0x001A, [UIntPtr]::Zero, "Environment", 0x0002, 1000, [ref]$ignored)
    }

    if (-not $Version) { $Version = $env:ISUZU_UNITY_CLI_VERSION }
    if (-not $Version) { $Version = "latest" }
    if ($Version -ne "latest" -and $Version -notmatch "^v") {
        $Version = "v$Version"
    }

    if (-not $InstallDir) { $InstallDir = $env:ISUZU_UNITY_CLI_DIR }
    if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA "Programs\isuzu-unity-cli" }

    # Test hook only: replaces the release download base with a local server so
    # the whole flow (download, hash check, install) can be exercised without
    # hitting GitHub. Not documented to end users.
    $baseUrl = $env:ISUZU_UNITY_CLI_BASE_URL
    if (-not $baseUrl) {
        if ($Version -eq "latest") {
            $baseUrl = "https://github.com/isuzu-shiranui/UnityMCP/releases/latest/download/"
        } else {
            $baseUrl = "https://github.com/isuzu-shiranui/UnityMCP/releases/download/$Version/"
        }
    }
    if (-not $baseUrl.EndsWith("/")) { $baseUrl = "$baseUrl/" }

    # Only x64 builds are published; RuntimeInformation catches an x86 process
    # running under WOW64 on an ARM64 host that PROCESSOR_ARCHITECTURE alone would miss.
    $procArch = $env:PROCESSOR_ARCHITECTURE
    $osArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    if ($procArch -ne "AMD64" -and $osArch -ne "X64") {
        throw "isuzu-unity-cli only ships x64 builds for Windows. Detected architecture: $osArch. No compatible asset is available."
    }

    $assetName = "isuzu-unity-cli-win-x64.exe"
    $exePath = Join-Path $InstallDir "isuzu-unity-cli.exe"

    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    $tempDir = Join-Path $env:TEMP "isuzu-unity-cli-install-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

    try {
        $tempAsset = Join-Path $tempDir $assetName
        $tempSums = Join-Path $tempDir "SHA256SUMS"

        Write-Host "Downloading $assetName ($Version)..."
        try {
            Invoke-WebRequest -Uri "$baseUrl$assetName" -OutFile $tempAsset -UseBasicParsing
            Invoke-WebRequest -Uri "${baseUrl}SHA256SUMS" -OutFile $tempSums -UseBasicParsing
        } catch {
            throw "Download failed for version '$Version': $($_.Exception.Message)"
        }

        $sumsLine = Get-Content $tempSums | Where-Object { $_ -match [regex]::Escape($assetName) + '\s*$' } | Select-Object -First 1
        if (-not $sumsLine) {
            throw "SHA256SUMS has no entry for $assetName. The release may be malformed."
        }
        $expectedHash = ($sumsLine -split '\s+')[0].ToUpperInvariant()
        $actualHash = (Get-FileHash -Path $tempAsset -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($actualHash -ne $expectedHash) {
            throw "Checksum mismatch for $assetName. Expected $expectedHash, got $actualHash. Aborting install."
        }
        Write-Host "Checksum verified."

        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

        # A running exe cannot be overwritten in place on Windows, so the old one
        # is moved aside first and only removed once the new one is in position.
        $oldExePath = "$exePath.old"
        if (Test-Path $oldExePath) { Remove-Item $oldExePath -Force -ErrorAction SilentlyContinue }
        if (Test-Path $exePath) { Rename-Item $exePath "$(Split-Path $exePath -Leaf).old" -Force }
        Move-Item $tempAsset $exePath -Force

        # `isuzu-unity-cli upgrade` runs this script from the very process that holds the renamed
        # file open, so on that path the delete cannot succeed and the line above clears it on the
        # next install instead. Saying so is the point: an unexplained .old file next to the
        # executable otherwise reads as a botched install.
        if (Test-Path $oldExePath) {
            try {
                Remove-Item $oldExePath -Force
            } catch {
                Write-Host "Left $oldExePath in place; it is still running. The next install removes it."
            }
        }

        if (Add-DirectoryToUserPath -Directory $InstallDir) {
            Send-EnvironmentChange
            Write-Host "Added $InstallDir to your user PATH. Open a new terminal for it to take effect."
        }
        if (($env:Path -split ";") -notcontains $InstallDir) {
            $env:Path = "$env:Path;$InstallDir"
        }

        Write-Host ""
        try {
            & $exePath --version
        } catch {
            Write-Warning "Installed, but '$exePath --version' failed to run: $($_.Exception.Message)"
        }

        Write-Host ""
        Write-Host "Next steps:"
        Write-Host "  1. Add the Unity package via Package Manager -> Add package from git URL:"
        Write-Host "     https://github.com/isuzu-shiranui/UnityMCP.git?path=jp.shiranui-isuzu.unity-mcp"
        Write-Host "  2. Run 'isuzu-unity-cli setup' to install the agent skill,"
        Write-Host "     or 'isuzu-unity-cli setup --mcp' to also register the MCP endpoint."
    } finally {
        Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
} $Version $InstallDir
