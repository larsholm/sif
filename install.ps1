param(
    [string]$RepoUrl = $(if ($env:SIF_INSTALL_REPO) { $env:SIF_INSTALL_REPO } else { "https://github.com/larsholm/sif" }),
    [string]$Ref = $(if ($env:SIF_INSTALL_REF) { $env:SIF_INSTALL_REF } else { "main" })
)

$ErrorActionPreference = "Stop"

function Require-Command {
    param([string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Missing required command: $Name"
    }
}

Require-Command dotnet

$packageId = "sif.agent"
$packageVersion = "1.0.0"
$extensionId = "sif.sif-vscode"
$extensionVersion = "0.1.0"
$archiveUrl = "$($RepoUrl.TrimEnd('/'))/archive/refs/heads/$Ref.zip"
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "sif-install-$([System.Guid]::NewGuid())"
$archivePath = Join-Path $tempDir "sif.zip"
$packageDir = Join-Path $tempDir "nupkg"

try {
    New-Item -ItemType Directory -Path $tempDir, $packageDir | Out-Null

    Write-Host "Downloading sif from $RepoUrl ($Ref)..."
    Invoke-WebRequest -Uri $archiveUrl -OutFile $archivePath
    Expand-Archive -Path $archivePath -DestinationPath $tempDir -Force

    $sourceDir = Get-ChildItem -Path $tempDir -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName "sif.agent/sif.agent.csproj") } |
        Select-Object -First 1

    if (-not $sourceDir) {
        throw "Downloaded archive did not contain sif.agent/sif.agent.csproj"
    }

    Write-Host "Building package..."
    dotnet pack (Join-Path $sourceDir.FullName "sif.agent/sif.agent.csproj") -c Release -o $packageDir

    Write-Host "Installing as global tool..."
    $installedTools = dotnet tool list --global
    if ($installedTools -match "^$([regex]::Escape($packageId))\s") {
        dotnet tool uninstall --global $packageId
    }

    $nugetPackageCache = Join-Path $HOME ".nuget/packages/$packageId/$packageVersion"
    if (Test-Path $nugetPackageCache) {
        Remove-Item -Recurse -Force $nugetPackageCache
    }

    dotnet tool install --source $packageDir --version $packageVersion --no-http-cache --global $packageId

    Write-Host "Installing VS Code extension..."
    $extensionSrc = Join-Path $sourceDir.FullName "sif.vscode"
    if (Test-Path $extensionSrc) {
        $extensionsRoot = if ($env:VSCODE_EXTENSIONS) { $env:VSCODE_EXTENSIONS } else { Join-Path $HOME ".vscode/extensions" }
        $extensionInstallDir = Join-Path $extensionsRoot "$extensionId-$extensionVersion"

        if (Test-Path $extensionInstallDir) {
            Remove-Item -Recurse -Force $extensionInstallDir
        }

        New-Item -ItemType Directory -Path $extensionInstallDir | Out-Null
        Copy-Item (Join-Path $extensionSrc "package.json") $extensionInstallDir
        Copy-Item (Join-Path $extensionSrc "extension.js") $extensionInstallDir
        Copy-Item (Join-Path $extensionSrc "README.md") $extensionInstallDir

        $vscodeIgnore = Join-Path $extensionSrc ".vscodeignore"
        if (Test-Path $vscodeIgnore) {
            Copy-Item $vscodeIgnore $extensionInstallDir
        }

        Write-Host "VS Code extension installed: $extensionInstallDir"
    } else {
        Write-Warning "VS Code extension source not found at $extensionSrc"
    }

    Write-Host ""
    Write-Host "Installed. Run 'sif' to start chatting, or use 'sif: Start Chat With Editor Context' in VS Code."
} finally {
    if (Test-Path $tempDir) {
        Remove-Item -Recurse -Force $tempDir
    }
}
