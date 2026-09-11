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
dotnet restore $solution --locked-mode
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build $solution --configuration $Configuration --no-restore -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet test $solution --configuration $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Target -eq 'publish') {
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $OutputDirectory = Join-Path $repoRoot ("artifacts\" + $Rid)
    }

    dotnet publish (Join-Path $repoRoot 'src\RuleVault.Cli\RuleVault.Cli.csproj') --configuration $Configuration --runtime $Rid --self-contained true --no-restore --output $OutputDirectory
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
