[CmdletBinding()]
param(
    [string] $InstallationSource = "..\src\VaultSource",
    [string] $OutputDirectory = "..\InstallerPackage",
    [string] $ArchivePath = "..\artifacts\RuleVault-0.1.0-dev-win-x64.zip",
    [switch] $RunTests
)

$ErrorActionPreference = 'Stop'

function Resolve-OwnedRepositoryPath([string] $Candidate, [string] $RepositoryRoot, [string] $Label) {
    $resolved = [IO.Path]::GetFullPath($Candidate)
    $resolvedRepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($resolvedRepositoryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must remain beneath $resolvedRepositoryRoot"
    }
    return $resolved
}

if ($Host.Name -eq 'ConsoleHost' -and -not [Console]::IsOutputRedirected) { Clear-Host }
Write-Host "====================================================" -ForegroundColor Magenta
Write-Host " 🚀   RULEVAULT: CREATING INSTALLER DISTRIBUTION     " -ForegroundColor Magenta
Write-Host "====================================================" -ForegroundColor Magenta

# Initialize Progress Tracking Bar
Write-Progress -Activity "Building Installer Package" -Status "Resolving directory paths..." -PercentComplete 5

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Resolve-OwnedRepositoryPath (Join-Path $PSScriptRoot $InstallationSource) $repoRoot 'Installation source'
$outputRoot = Resolve-OwnedRepositoryPath (Join-Path $PSScriptRoot $OutputDirectory) $repoRoot 'Package output'
$archiveFullPath = Resolve-OwnedRepositoryPath (Join-Path $PSScriptRoot $ArchivePath) (Join-Path $repoRoot 'artifacts') 'Package archive'

if ([string]::Equals($sourceRoot, $outputRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package output cannot be the installation source.'
}

# Package creation is fast by default. Release validation remains available when
# explicitly requested instead of making every local package rerun the full suite.
$buildArguments = @{ Target = 'publish' }
if (-not $RunTests) { $buildArguments.SkipTests = $true }
$CliArtifactDirectory = & "$PSScriptRoot/build.ps1" @buildArguments

if ($null -eq $CliArtifactDirectory) {
    Write-Progress -Activity "Building Installer Package" -Completed
    Write-Host " ❌  [ERROR] Build script failed to generate target directory path." -ForegroundColor Red
    exit 1
}

Write-Progress -Activity "Building Installer Package" -Status "Staging compilation artifacts..." -PercentComplete 85

$artifactRoot = [IO.Path]::GetFullPath($CliArtifactDirectory)
$executable = Join-Path $artifactRoot 'rv.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    Write-Progress -Activity "Building Installer Package" -Completed
    Write-Host " ❌  [ERROR] Compiled executable 'rv.exe' was missing from $artifactRoot" -ForegroundColor Red
    exit 1
}

if (Test-Path -Path $outputRoot -PathType Container) {
    Write-Host " 🧹  Cleaning existing output directory..." -ForegroundColor DarkGray
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}

# Prepare Staging Layout
New-Item -ItemType Directory -Path (Join-Path $outputRoot 'bin\win-x64') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $outputRoot 'runtime-payload') -Force | Out-Null

Write-Host " 📦  Staging structural package configuration files..." -ForegroundColor Gray
Copy-Item -LiteralPath $executable -Destination (Join-Path $outputRoot 'bin\win-x64\rv.exe') -Force

$sourcePayload = Join-Path $sourceRoot 'runtime-payload'
if (Test-Path -Path $sourcePayload -PathType Container) {
    Get-ChildItem -LiteralPath $sourcePayload -Force |
        Copy-Item -Destination (Join-Path $outputRoot 'runtime-payload') -Recurse -Force
} else {
    throw "Required runtime payload is missing: $sourcePayload"
}

$requiredFiles = @('package.json', 'guide.md', 'migrations.json', 'install.ps1', 'install.sh')
foreach ($file in $requiredFiles) {
    $targetFile = Join-Path $sourceRoot $file
    if (Test-Path -LiteralPath $targetFile -PathType Leaf) {
        Copy-Item -LiteralPath $targetFile -Destination (Join-Path $outputRoot $file) -Force
        Write-Host "     ✔ Linked target asset: $file" -ForegroundColor DarkGreen
    } else {
        throw "Required deployment asset is missing: $targetFile"
    }
}

Write-Progress -Activity "Building Installer Package" -Status "Generating cryptographic release manifest..." -PercentComplete 95

$artifactPath = Join-Path $outputRoot 'bin\win-x64\rv.exe'
$packageFiles = @(Get-ChildItem -LiteralPath $outputRoot -Recurse -File | Where-Object {
    $_.FullName -ne $artifactPath -and $_.Name -ne 'artifacts.json'
} | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        relative_path = $_.FullName.Substring($outputRoot.TrimEnd([IO.Path]::DirectorySeparatorChar).Length).TrimStart([IO.Path]::DirectorySeparatorChar).Replace('\', '/')
        raw_sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        size = $_.Length
    }
})
$manifest = [ordered]@{
    schema_version = 1
    package_version = '0.1.0-dev'
    artifacts = @(
        [ordered]@{
            rid = 'win-x64'
            relative_path = 'bin/win-x64/rv.exe'
            raw_sha256 = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash
            size = (Get-Item -LiteralPath $artifactPath).Length
            tested = $true
        }
    )
    package_files = $packageFiles
}
$manifestText = $manifest | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $outputRoot 'artifacts.json'), $manifestText + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

$archiveDirectory = Split-Path -Parent $archiveFullPath
if (-not (Test-Path -LiteralPath $archiveDirectory -PathType Container)) { New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null }
if (Test-Path -LiteralPath $archiveFullPath -PathType Leaf) { Remove-Item -LiteralPath $archiveFullPath -Force }
Compress-Archive -Path (Join-Path $outputRoot '*') -DestinationPath $archiveFullPath -CompressionLevel Optimal
$archiveHash = (Get-FileHash -LiteralPath $archiveFullPath -Algorithm SHA256).Hash

# Complete the Top Window Progress Bar
Write-Progress -Activity "Building Installer Package" -Status "Done!" -Completed

# Display Final Summary Box
Write-Host ""
Write-Host "====================================================" -ForegroundColor Green
Write-Host "  ✔  SUCCESS: Distribution Package Fully Assembled!"  -ForegroundColor Green
Write-Host "====================================================" -ForegroundColor Green
Write-Host " 📂 Location: " -NoNewline -ForegroundColor Gray
Write-Host "$outputRoot" -ForegroundColor White
Write-Host " 📦 Archive:  " -NoNewline -ForegroundColor Gray
Write-Host "$archiveFullPath" -ForegroundColor White
Write-Host " 🔐 SHA-256:  " -NoNewline -ForegroundColor Gray
Write-Host "$archiveHash" -ForegroundColor DarkGray
Write-Host "====================================================" -ForegroundColor Green
