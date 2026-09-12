[CmdletBinding()]
param(
    [ValidateSet('verify', 'publish')]
    [string] $Target = 'verify',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $Rid = 'win-x64',
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'RuleVaultCli.sln'

function Resolve-OwnedBuildPath([string] $Candidate, [string] $OwnedRoot, [string] $Label) {
    $resolved = [IO.Path]::GetFullPath($Candidate)
    $resolvedOwnedRoot = [IO.Path]::GetFullPath($OwnedRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($resolvedOwnedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must remain beneath $resolvedOwnedRoot"
    }
    return $resolved
}

# Progress Bar Update - Stage 2: Compiling Code
Write-Progress -Activity "Building Installer Package" -Status "Restoring solution dependencies..." -PercentComplete 20

Write-Host "  ⚙  [1/4] Restoring solution: $solution" -ForegroundColor Cyan
dotnet restore $solution --locked-mode | Out-Host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Progress -Activity "Building Installer Package" -Status "Building .NET Solution binaries..." -PercentComplete 40
Write-Host "  ⚙  [2/4] Building solution: $solution" -ForegroundColor Cyan
dotnet build $solution --configuration $Configuration --no-restore -p:ContinuousIntegrationBuild=true | Out-Host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Progress -Activity "Building Installer Package" -Status "Running test fixtures..." -PercentComplete 60
Write-Host "  ⚙  [3/4] Running tests for solution: $solution" -ForegroundColor Cyan
dotnet test $solution --configuration $Configuration --no-restore | Out-Host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Target -eq 'publish') {
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $OutputDirectory = Join-Path $repoRoot ("artifacts\" + $Rid)
    }
    $OutputDirectory = Resolve-OwnedBuildPath $OutputDirectory (Join-Path $repoRoot 'artifacts') 'Publish output'
    if (Test-Path -Path $OutputDirectory -PathType Container) {
        Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
    }

    Write-Progress -Activity "Building Installer Package" -Status "Publishing self-contained distribution..." -PercentComplete 75
    Write-Host "  ⚙  [4/4] Publishing CLI binaries..." -ForegroundColor Cyan
    dotnet publish (Join-Path $repoRoot 'src\RuleVault.Cli\RuleVault.Cli.csproj') --configuration $Configuration --runtime $Rid --self-contained true --no-restore --output $OutputDirectory | Out-Host
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    return $OutputDirectory
}
return $null
