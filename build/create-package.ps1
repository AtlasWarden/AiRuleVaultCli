[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $CliArtifactDirectory,
    [Parameter(Mandatory = $true)]
    [string] $InstallationSource,
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath($CliArtifactDirectory)
$sourceRoot = [IO.Path]::GetFullPath($InstallationSource)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$stalePlan = Join-Path $outputRoot 'install.plan.json'
if (Test-Path -LiteralPath $stalePlan -PathType Leaf) {
    Remove-Item -LiteralPath $stalePlan -Force
}
$executable = Join-Path $artifactRoot 'rv.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'The explicit CLI artifact directory does not contain rv.exe.'
}

New-Item -ItemType Directory -Path (Join-Path $outputRoot 'bin\win-x64') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $outputRoot 'runtime-payload') -Force | Out-Null
Copy-Item -LiteralPath $executable -Destination (Join-Path $outputRoot 'bin\win-x64\rv.exe') -Force
Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'runtime-payload') -Force |
    Copy-Item -Destination (Join-Path $outputRoot 'runtime-payload') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot 'package.json') -Destination (Join-Path $outputRoot 'package.json') -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot 'guide.md') -Destination (Join-Path $outputRoot 'guide.md') -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot 'migrations.json') -Destination (Join-Path $outputRoot 'migrations.json') -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot 'install.ps1') -Destination (Join-Path $outputRoot 'install.ps1') -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot 'install.sh') -Destination (Join-Path $outputRoot 'install.sh') -Force

$artifactPath = Join-Path $outputRoot 'bin\win-x64\rv.exe'
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
}
$manifestText = $manifest | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $outputRoot 'artifacts.json'), $manifestText + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
