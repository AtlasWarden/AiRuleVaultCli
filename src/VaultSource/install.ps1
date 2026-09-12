[CmdletBinding()]
param(
    [string] $PackageRoot,
    [string] $VaultRoot,
    [string] $ConfigRoot,
    [string] $VaultId,
    [string] $PlanPath,
    [switch] $Apply,
    [switch] $NonInteractive,
    [switch] $BootstrapAll,
    [string] $BootstrapAdapter,
    [string] $BootstrapHomeRoot,
    [ValidateSet('restore-package', 'accept-current')]
    [string] $IntegrityRepairStrategy
)

$ErrorActionPreference = 'Stop'
$script:ChangesApproved = $false
$script:IntegrityRepairOutcome = 'not-attempted'

function Write-InstallerHeader([string] $Status = '', [int] $Percent = 0) {
    if (-not $NonInteractive) {
        Clear-Host
        if ($Host.Name -eq 'ConsoleHost') {
            $Host.UI.RawUI.CursorPosition = [System.Management.Automation.Host.Coordinates]::new(0, 0)
        }
    }
    
    Write-Host "====================================================" -ForegroundColor Magenta
    Write-Host " 🚀   RULEVAULT: SECURE SYSTEM WORKSPACE SETUP       " -ForegroundColor Magenta
    Write-Host "====================================================" -ForegroundColor Magenta
    Write-Host '  Rule Vault checks everything before it changes your files.' -ForegroundColor Gray
    Write-Host '  Nothing changes until you say yes.' -ForegroundColor Gray
    Write-Host "----------------------------------------------------" -ForegroundColor DarkGray
    
    if (-not [string]::IsNullOrWhiteSpace($Status)) {
        $filled = [Math]::Min(10, [Math]::Max(0, [Math]::Ceiling($Percent / 10.0)))
        $empty = 10 - $filled
        
        Write-Host "  Status: [" -NoNewline -ForegroundColor DarkGray
        Write-Host ('■' * $filled) -NoNewline -ForegroundColor Green
        Write-Host ('·' * $empty) -NoNewline -ForegroundColor DarkGray
        Write-Host "] ($Percent%) " -NoNewline -ForegroundColor White
        Write-Host "▶ $Status" -ForegroundColor Cyan
        Write-Host "----------------------------------------------------" -ForegroundColor DarkGray
    }
    Write-Host ''
}

function Update-InstallerProgress([int] $Percent, [string] $Status) {
    if (-not $NonInteractive) { Write-InstallerHeader $Status $Percent }
}

function Complete-InstallerProgress { }

function Show-PlanBlocked([object] $Result, [int] $ExitCode) {
    if ($NonInteractive) {
        $Result | Write-Output
        exit $ExitCode
    }

    $failure = $null
    try { $failure = $Result | Out-String | ConvertFrom-Json } catch { }
    $message = 'The selected vault needs attention before it can be updated. No files were changed.'
    if ($failure -and $failure.code -eq 'REGISTRY_ROOT_MISMATCH') {
        $message = 'This folder belongs to a different vault registration. Choose its registered location before updating.'
    }

    Write-InstallerHeader 'Needs attention before updating' 70
    Write-Host "  ❌ [BLOCKED] $message" -ForegroundColor Red
    Write-Host '  Your existing vault and agent settings were not changed.' -ForegroundColor Gray
}

function Show-PlainPlan([object] $Plan, [string] $Operation, [switch] $Repair) {
    Write-InstallerHeader 'Ready for your approval' 80
    if ($Repair) {
        Write-Host '  Rule Vault found saved fingerprints that do not match the current files.' -ForegroundColor White
        Write-Host '  It will archive the old records, calculate new fingerprints, and continue.' -ForegroundColor White
        Write-Host '  Your rules, context, skills, roles, projects, and daily notes will be kept.' -ForegroundColor Gray
    } elseif ($Operation -eq 'Install a new vault') {
        Write-Host '  Rule Vault is ready to create the vault and install its command-line tool.' -ForegroundColor White
    } else {
        Write-Host '  Rule Vault is ready to update its managed files and command-line tool.' -ForegroundColor White
        Write-Host '  Your rules, context, skills, roles, projects, and daily notes will be kept.' -ForegroundColor Gray
    }

    $changeCount = @($Plan.operations).Count
    Write-Host "  The plan contains $changeCount file and setup change(s)." -ForegroundColor Gray
    Write-Host "  Plan: $($Plan.plan_path)" -ForegroundColor DarkGray
    Write-Host ''
}

function Confirm-PlanChanges {
    if ($NonInteractive) { return [bool]$Apply }
    if ($script:ChangesApproved) { return $true }
    if ((Read-Choice 'Are these changes okay? Y/N' @('Y', 'N') 'Y') -ne 'Y') { return $false }
    $script:ChangesApproved = $true
    return $true
}

function Invoke-IntegrityRepair([string] $CliPath, [string] $PackageRoot, [string] $VaultRoot, [string] $ConfigRoot, [string] $VaultId, [string] $Strategy, [object] $Failure) {
    $script:IntegrityRepairOutcome = 'blocked'
    if ([string]::IsNullOrWhiteSpace($Strategy)) {
        $Strategy = 'accept-current'
    }

    $repairPlanPath = Join-Path (Join-Path ([IO.Path]::GetTempPath()) 'RuleVault') ("repair-plan-$VaultId.json")
    $repairPlanDirectory = Split-Path -Parent $repairPlanPath
    if (-not (Test-Path -LiteralPath $repairPlanDirectory)) { New-Item -ItemType Directory -Path $repairPlanDirectory -Force | Out-Null }

    Update-InstallerProgress 72 'Preparing a safe repair'
    $repairResult = & $CliPath repair plan --vault-root $VaultRoot --config-root $ConfigRoot --vault-id $VaultId --package-root $PackageRoot --strategy $Strategy --output $repairPlanPath --format json
    if ($LASTEXITCODE -ne 0) {
        Show-PlanBlocked $repairResult $LASTEXITCODE
        return $false
    }

    $repairPlan = Get-Content -LiteralPath $repairPlanPath -Raw | ConvertFrom-Json
    $repairPlan | Add-Member -NotePropertyName plan_path -NotePropertyValue $repairPlanPath -Force
    Show-PlainPlan $repairPlan 'Update the existing vault' -Repair
    if (-not (Confirm-PlanChanges)) {
        if ($NonInteractive) {
            Write-Host '  No files were changed. Run again with -Apply to approve this plan.' -ForegroundColor Yellow
            $script:IntegrityRepairOutcome = 'planned'
        } else {
            Write-Host '  Okay. Nothing was changed.' -ForegroundColor Gray
            $script:IntegrityRepairOutcome = 'cancelled'
        }
        return $false
    }

    $decision = @($repairPlan.required_decisions)[0]
    $repairApply = & $CliPath plan apply $repairPlanPath --approve $repairPlan.plan_sha256 --accept-decision $decision --format json
    if ($LASTEXITCODE -ne 0 -or (($repairApply | Out-String | ConvertFrom-Json).code) -ne 'OK') {
        throw 'The integrity repair could not be committed. Existing recovery archives and the repair plan were retained.'
    }

    Update-InstallerProgress 76 'Mismatch fixed; continuing the update'
    $script:IntegrityRepairOutcome = 'applied'
    return $true
}

function Confirm-InstalledVaultReady([string] $CliPath, [string] $PackageRoot, [string] $VaultRoot, [string] $ConfigRoot, [string] $VaultId, [string] $GuideSha256, [string] $Strategy) {
    $verificationPlanPath = Join-Path (Join-Path ([IO.Path]::GetTempPath()) 'RuleVault') ("post-install-check-$VaultId.json")
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        Update-InstallerProgress 93 'Checking that agents can use the updated vault'
        $verificationResult = & $CliPath update plan --vault-root $VaultRoot --config-root $ConfigRoot --vault-id $VaultId --package-root $PackageRoot --guide-sha256 $GuideSha256 --output $verificationPlanPath --format json
        $verificationExitCode = $LASTEXITCODE
        $verification = $null
        try { $verification = $verificationResult | Out-String | ConvertFrom-Json } catch { }

        if ($verificationExitCode -eq 0 -and $verification -and $verification.code -eq 'OK') {
            $remainingOperations = @($verification.data.plan.operations)
            if ($remainingOperations.Count -ne 0) {
                throw 'The installer finished its plan, but another update is still required. No success was reported; rerun the installer to review the new plan.'
            }
            return
        }

        $repairable = $verification -and $verification.code -in @(
            'INTEGRITY_ANCHOR_MISMATCH',
            'INTEGRITY_HASH_MISMATCH',
            'VAULT_REGISTRY_INTEGRITY_MISMATCH'
        )
        if ($attempt -eq 0 -and $repairable -and (Invoke-IntegrityRepair $CliPath $PackageRoot $VaultRoot $ConfigRoot $VaultId $Strategy $verification)) {
            continue
        }

        Show-PlanBlocked $verificationResult $verificationExitCode
        throw 'The updated vault did not pass its final registry and protected-file check. The CLI was not replaced and success was not reported.'
    }

    throw 'The updated vault could not be made ready for agent use.'
}

function Confirm-AgentContextReady([string] $CliPath, [string] $ConfigRoot) {
    $sessionId = 'installer-check-' + [guid]::NewGuid().ToString('N')
    $registered = $false
    try {
        Update-InstallerProgress 97 'Checking that agents can load the vault'
        $registrationResult = & $CliPath agent register --config-root $ConfigRoot --session-id $sessionId --name 'Rule Vault installer check' --folder ([IO.Path]::GetTempPath()) --project global --agent-kind installer --format json
        $registrationExitCode = $LASTEXITCODE
        $registration = $null
        try { $registration = $registrationResult | Out-String | ConvertFrom-Json } catch { }
        if ($registrationExitCode -ne 0 -or -not $registration -or $registration.code -ne 'OK') {
            throw 'The installed CLI could not register an agent verification session. Success was not reported.'
        }
        $registered = $true

        $contextResult = & $CliPath agent context --config-root $ConfigRoot --session-id $sessionId --operation maintain-vault --subjects 'rules,project' --paths 'AGENTS.md' --format json
        $contextExitCode = $LASTEXITCODE
        $context = $null
        try { $context = $contextResult | Out-String | ConvertFrom-Json } catch { }
        if ($contextExitCode -ne 0 -or -not $context -or $context.code -ne 'OK') {
            $reason = if ($context -and $context.code) { $context.code } else { 'unknown error' }
            throw "The installed vault could not load required agent context ($reason). Success was not reported."
        }
    } finally {
        if ($registered) {
            & $CliPath agent clear --config-root $ConfigRoot --session-id $sessionId --format json | Out-Null
        }
    }
}

function Read-Choice([string] $Prompt, [string[]] $Allowed, [string] $Default = '') {
    while ($true) {
        $defaultHint = if ([string]::IsNullOrWhiteSpace($Default)) { '' } else { " [Enter = $($Default.ToUpperInvariant())]" }
        $choice = (Read-Host "      $Prompt$defaultHint").Trim().ToUpperInvariant()
        if ([string]::IsNullOrWhiteSpace($choice) -and -not [string]::IsNullOrWhiteSpace($Default) -and $Allowed -contains $Default.ToUpperInvariant()) {
            return $Default.ToUpperInvariant()
        }
        if ($Allowed -contains $choice) { return $choice }
        $retry = "  ⚠️ Please choose: $($Allowed -join ', ')"
        if (-not [string]::IsNullOrWhiteSpace($Default)) { $retry += ", or press Enter for $($Default.ToUpperInvariant())" }
        Write-Host $retry -ForegroundColor Yellow
    }
}

function Complete-InstallerPath([string] $InputText) {
    if ([string]::IsNullOrWhiteSpace($InputText)) { return $InputText }
    try {
        $candidate = [Environment]::ExpandEnvironmentVariables($InputText.Trim().Trim('"'))
        $fullCandidate = [IO.Path]::GetFullPath($candidate)
        $endsWithSeparator = $candidate.EndsWith([IO.Path]::DirectorySeparatorChar.ToString()) -or $candidate.EndsWith([IO.Path]::AltDirectorySeparatorChar.ToString())
        $parent = if ($endsWithSeparator) { $fullCandidate } else { Split-Path -Parent $fullCandidate }
        $leaf = if ($endsWithSeparator) { '' } else { Split-Path -Leaf $fullCandidate }
        if ([string]::IsNullOrWhiteSpace($parent) -or -not (Test-Path -LiteralPath $parent -PathType Container)) { return $InputText }
        $matches = @(Get-ChildItem -LiteralPath $parent -Force -ErrorAction Stop | Where-Object { $_.Name.StartsWith($leaf, [StringComparison]::OrdinalIgnoreCase) } | Sort-Object @{ Expression = { -not $_.PSIsContainer } }, Name)
        if ($matches.Count -ne 1) { return $InputText }
        $completed = $matches[0].FullName
        if ($matches[0].PSIsContainer) { $completed += [IO.Path]::DirectorySeparatorChar }
        return $completed
    } catch {
        return $InputText
    }
}

function Read-RequiredPath([string] $Label, [string] $Suggested) {
    while ($true) {
        if ($Host.Name -ne 'ConsoleHost') {
            $answer = (Read-Host "      ▶ $Label [$Suggested]").Trim()
        } else {
            $prompt = "      ▶ $Label (Enter uses the suggested path): "
            $answer = ''
            $cursor = 0
            $row = $Host.UI.RawUI.CursorPosition.Y
            $render = {
                $width = [Math]::Max(20, $Host.UI.RawUI.BufferSize.Width - 1)
                $whole = $prompt + $answer
                $absoluteCursor = $prompt.Length + $cursor
                $viewStart = [Math]::Max(0, $absoluteCursor - $width + 1)
                if ($whole.Length - $viewStart -gt $width) { $visible = $whole.Substring($viewStart, $width) } else { $visible = $whole.Substring($viewStart) }
                $Host.UI.RawUI.CursorPosition = [System.Management.Automation.Host.Coordinates]::new(0, $row)
                Write-Host $visible.PadRight($width) -NoNewline
                $cursorColumn = [Math]::Min($width - 1, [Math]::Max(0, $absoluteCursor - $viewStart))
                $Host.UI.RawUI.CursorPosition = [System.Management.Automation.Host.Coordinates]::new($cursorColumn, $row)
            }
            & $render
            :pathInput while ($true) {
                $key = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
                switch ($key.VirtualKeyCode) {
                    13 { & $render; Write-Host ''; break pathInput }
                    8  { if ($cursor -gt 0) { $answer = $answer.Remove($cursor - 1, 1); $cursor-- }; & $render; continue }
                    9  { $answer = Complete-InstallerPath $answer; $cursor = $answer.Length; & $render; continue }
                    35 { $cursor = $answer.Length; & $render; continue }
                    36 { $cursor = 0; & $render; continue }
                    37 { if ($cursor -gt 0) { $cursor-- }; & $render; continue }
                    39 { if ($cursor -lt $answer.Length) { $cursor++ }; & $render; continue }
                    46 { if ($cursor -lt $answer.Length) { $answer = $answer.Remove($cursor, 1) }; & $render; continue }
                }
                if (-not [char]::IsControl($key.Character)) {
                    $answer = $answer.Insert($cursor, $key.Character.ToString())
                    $cursor++
                    & $render
                }
            }
            $answer = $answer.Trim()
        }
        if ([string]::IsNullOrWhiteSpace($answer)) { return $Suggested }
        try { return [IO.Path]::GetFullPath($answer) }
        catch { Write-Host '  ❌ That is not a valid Windows path.' -ForegroundColor Red }
    }
}

function Get-CanonicalTextSha256([string] $Path) {
    $text = [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false, $true))
    if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) { $text = $text.Substring(1) }
    $text = $text -replace "`r`n", "`n" -replace "`r", "`n"
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return -join ($sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)) | ForEach-Object { $_.ToString('X2') })
    } finally {
        $sha256.Dispose()
    }
}

function Get-RegistryManifestState([string] $VaultRoot, [string] $ConfigRoot, [string] $VaultId) {
    $manifestPath = Join-Path $VaultRoot '.vault-system\content-integrity.json'
    $registryPath = Join-Path $ConfigRoot 'vault-registry.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or -not (Test-Path -LiteralPath $registryPath -PathType Leaf)) {
        throw 'The installed vault is missing its integrity manifest or registry.'
    }

    $manifestSha256 = Get-CanonicalTextSha256 $manifestPath
    $registry = Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json
    $entries = @($registry.vaults | Where-Object { [string]$_.vault_id -eq $VaultId })
    if ($entries.Count -ne 1) { throw 'The installed vault has no single matching registry entry.' }
    $recordedSha256 = [string]$entries[0].protected_content_manifest_sha256
    return [pscustomobject]@{
        Matches = [string]::Equals($recordedSha256, $manifestSha256, [StringComparison]::OrdinalIgnoreCase)
        RecordedSha256 = $recordedSha256
        ManifestSha256 = $manifestSha256
    }
}

function Get-RuleVaultUserPathUpdate([string] $CurrentPath, [string] $CliDirectory) {
    if ($CliDirectory.Contains(';')) {
        throw 'The Rule Vault CLI folder contains a semicolon and cannot be safely added to PATH.'
    }

    $fullCliDirectory = [IO.Path]::GetFullPath($CliDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $entries = @($CurrentPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $alreadyPresent = $false
    foreach ($entry in $entries) {
        try {
            $expanded = [Environment]::ExpandEnvironmentVariables($entry.Trim())
            $normalized = [IO.Path]::GetFullPath($expanded).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
            if ([string]::Equals($normalized, $fullCliDirectory, [StringComparison]::OrdinalIgnoreCase)) {
                $alreadyPresent = $true
                break
            }
        } catch {
            # Keep unrelated PATH entries exactly as the user configured them.
        }
    }

    if (-not $alreadyPresent) { $entries += $fullCliDirectory }
    return [pscustomobject]@{ Value = if ($alreadyPresent) { $CurrentPath } else { $entries -join ';' }; Added = -not $alreadyPresent }
}

function Get-RuleVaultUserPathValue {
    return [Environment]::GetEnvironmentVariable('Path', [EnvironmentVariableTarget]::User)
}

function Set-RuleVaultUserPathValue([string] $Value) {
    [Environment]::SetEnvironmentVariable('Path', $Value, [EnvironmentVariableTarget]::User)
}

function Add-RuleVaultToUserPath([string] $CliDirectory) {
    $userPath = Get-RuleVaultUserPathValue
    $userUpdate = Get-RuleVaultUserPathUpdate $userPath $CliDirectory
    if ($userUpdate.Added) {
        Set-RuleVaultUserPathValue $userUpdate.Value
    }

    $processUpdate = Get-RuleVaultUserPathUpdate $env:Path $CliDirectory
    if ($processUpdate.Added) { $env:Path = $processUpdate.Value }

    return $userUpdate.Added
}

function Install-TrustedCli([string] $VerifiedArtifact, [string] $ExpectedSha256, [string] $ConfigRoot, [string] $GuideHash) {
    $cliDirectory = Join-Path $ConfigRoot 'cli'
    $cliPath = Join-Path $cliDirectory 'rulevault.exe'
    $legacyCliPath = Join-Path $cliDirectory 'rv.exe'
    $descriptorPath = Join-Path $ConfigRoot 'rule-vault-cli.json'
    New-Item -ItemType Directory -Path $cliDirectory -Force | Out-Null

    $removeOwnedLegacyCli = $false
    if ((Test-Path -LiteralPath $legacyCliPath -PathType Leaf) -and (Test-Path -LiteralPath $descriptorPath -PathType Leaf)) {
        try {
            $oldDescriptor = Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json
            $oldExecutablePath = [IO.Path]::GetFullPath([string]$oldDescriptor.executable_path)
            $oldExpectedHash = [string]$oldDescriptor.executable_raw_sha256
            $removeOwnedLegacyCli = [string]::Equals($oldExecutablePath, [IO.Path]::GetFullPath($legacyCliPath), [StringComparison]::OrdinalIgnoreCase) -and
                -not [string]::IsNullOrWhiteSpace($oldExpectedHash) -and
                [string]::Equals((Get-FileHash -LiteralPath $legacyCliPath -Algorithm SHA256).Hash, $oldExpectedHash, [StringComparison]::OrdinalIgnoreCase)
        } catch {
            # An unknown or damaged legacy executable is user evidence; leave it untouched.
            $removeOwnedLegacyCli = $false
        }
    }

    $stage = Join-Path $cliDirectory ('.rulevault.' + [guid]::NewGuid().ToString('N') + '.stage')
    try {
        Copy-Item -LiteralPath $VerifiedArtifact -Destination $stage -Force
        $stageHash = (Get-FileHash -LiteralPath $stage -Algorithm SHA256).Hash
        if (-not [string]::Equals($stageHash, $ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The staged CLI artifact did not match the verified package digest.'
        }
        Move-Item -LiteralPath $stage -Destination $cliPath -Force
    } finally {
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Force }
    }

    if ($removeOwnedLegacyCli) {
        Remove-Item -LiteralPath $legacyCliPath -Force
    }

    $pathAdded = Add-RuleVaultToUserPath $cliDirectory

    $descriptor = [ordered]@{
        schema_version = 1
        executable_path = $cliPath
        executable_raw_sha256 = $ExpectedSha256.ToUpperInvariant()
        capabilities_command = @('agent', 'capabilities', '--format', 'json')
        guide_sha256 = $GuideHash.ToUpperInvariant()
        updated_at = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json -Depth 3
    
    $descriptorStage = $descriptorPath + '.' + [guid]::NewGuid().ToString('N') + '.stage'
    try {
        [IO.File]::WriteAllText($descriptorStage, $descriptor + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $descriptorStage -Destination $descriptorPath -Force
    } finally {
        if (Test-Path -LiteralPath $descriptorStage) { Remove-Item -LiteralPath $descriptorStage -Force }
    }
    return [pscustomobject]@{ CliPath = $cliPath; DescriptorPath = $descriptorPath; PathAdded = $pathAdded }
}

function Invoke-RuleVaultBootstrap([string] $CliPath, [string] $ConfigRoot, [string] $Adapter, [bool] $All, [string] $HomeRoot = '') {
    $arguments = @('agents', 'bootstrap', 'apply', '--config-root', $ConfigRoot, '--format', 'json')
    if ($All) { $arguments += @('--all', 'true') } else { $arguments += @('--adapter', $Adapter) }
    if (-not [string]::IsNullOrWhiteSpace($HomeRoot)) { $arguments += @('--home-root', $HomeRoot) }
    $result = & $CliPath @arguments
    if ($LASTEXITCODE -ne 0) { throw 'The Rule Vault CLI could not apply the selected agent bootstrap.' }
    return ($result | Out-String | ConvertFrom-Json)
}

function Show-RuleVaultBootstrapMenu([string] $CliPath, [string] $ConfigRoot, [string] $HomeRoot = '') {
    $discoveryArguments = @('agents', 'bootstrap', 'discover', '--format', 'json')
    if (-not [string]::IsNullOrWhiteSpace($HomeRoot)) { $discoveryArguments += @('--home-root', $HomeRoot) }
    $discoveryResult = & $CliPath @discoveryArguments
    if ($LASTEXITCODE -ne 0) {
        Write-Host '  Agent bootstrap discovery could not run. Your vault installation is complete.' -ForegroundColor Yellow
        return
    }
    $discovery = $discoveryResult | Out-String | ConvertFrom-Json
    $targets = @($discovery.data.targets)
    $applicable = @($targets | Where-Object { $_.status -in @('ready-add', 'ready-update', 'legacy-replace') })
    
    Write-InstallerHeader 'Optional step — agent bootstrap' 100
    Write-Host '  Rule Vault found these known agent configuration targets:' -ForegroundColor White
    if ($targets.Count -eq 0) { Write-Host '  No known agent targets were found.' -ForegroundColor DarkGray; return }
    
    for ($index = 0; $index -lt $targets.Count; $index++) {
        $target = $targets[$index]
        $color = if ($target.status -in @('ready-add', 'ready-update', 'legacy-replace')) { 'Green' } else { 'DarkGray' }
        Write-Host ("  [{0}] {1} — {2}" -f ($index + 1), $target.adapter_id, $target.status) -ForegroundColor $color
    }
    if ($applicable.Count -eq 0) { Write-Host '  Nothing can be safely changed automatically.' -ForegroundColor DarkGray; return }
    Write-Host ''
    
    $allowed = @('A', 'S') + (1..$targets.Count | ForEach-Object { $_.ToString() })
    $choice = Read-Choice 'Select A for all safe targets, a number for one target, or S to skip' $allowed
    if ($choice -eq 'S') { return }
    if ($choice -eq 'A') {
        $result = Invoke-RuleVaultBootstrap $CliPath $ConfigRoot '' $true $HomeRoot
        Write-Host "  ✔ Updated $(@($result.data | Where-Object { $_.changed }).Count) agent bootstrap target(s)." -ForegroundColor Green
        return
    }
    $target = $targets[[int]$choice - 1]
    if ($target.status -notin @('ready-add', 'ready-update', 'legacy-replace')) {
        Write-Host '  That target was not changed because it is absent, ambiguous, or conflicting.' -ForegroundColor Yellow
        return
    }
    $result = Invoke-RuleVaultBootstrap $CliPath $ConfigRoot $target.adapter_id $false $HomeRoot
    Write-Host "  ✔ $($result.data[0].adapter_id): $($result.data[0].status)" -ForegroundColor Green
}

function Write-MenuOption([string] $Text, [bool] $Selected) {
    if ($Selected) { Write-Host "  ➔ $Text" -ForegroundColor Green } else { Write-Host "     $Text" -ForegroundColor White }
}

function Write-LocationMenu([int] $Selected) {
    Write-InstallerHeader 'Step 1 of 4 — Choose an installation location' 20
    Write-Host '  Where should Rule Vault be installed?' -ForegroundColor White
    Write-MenuOption '[1] Use the recommended location' ($Selected -eq 0)
    if ($Selected -eq 0) { Write-Host "      Vault:  $defaultVaultRoot" -ForegroundColor DarkGray }
    Write-MenuOption '[2] Choose a different vault location' ($Selected -eq 1)
    if ($Selected -eq 1) { Write-Host "      Vault folder [$defaultVaultRoot]" -ForegroundColor DarkGray }
    Write-MenuOption '[Q] Quit' ($Selected -eq 2)
    Write-Host ''
    Write-Host '  ↑/↓ move   Enter select   1, 2, or Q choose an item' -ForegroundColor DarkGray
}

function Read-LocationMenu {
    $selected = 0
    if ($Host.Name -ne 'ConsoleHost') {
        Write-LocationMenu $selected
        $fallback = Read-Choice '  Select 1, 2, or Q' @('1', '2', 'Q') '1'
        return @{ '1' = 0; '2' = 1; 'Q' = 2 }[$fallback]
    }
    while ($true) {
        Write-LocationMenu $selected
        $key = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
        switch ($key.VirtualKeyCode) {
            38 { $selected = ($selected + 2) % 3; continue }
            40 { $selected = ($selected + 1) % 3; continue }
            13 { return $selected }
            27 { return 2 }
        }
        switch ($key.Character.ToString().ToUpperInvariant()) {
            '1' { return 0 }
            '2' { return 1 }
            'Q' { return 2 }
        }
    }
}

function Read-CustomLocations {
    Write-LocationMenu 1
    Write-Host ''
    Write-Host '      Leave a field blank to use its suggested location.' -ForegroundColor DarkGray
    $customVaultRoot = Read-RequiredPath 'Vault folder' $defaultVaultRoot
    return [pscustomobject]@{ VaultRoot = $customVaultRoot }
}

$defaultPackageRoot = $PSScriptRoot
$defaultVaultRoot = Join-Path $env:USERPROFILE '.ai-rule-vault'
$defaultConfigRoot = Join-Path $env:LOCALAPPDATA 'AI-Rule-Vault'
$providedLocations = -not [string]::IsNullOrWhiteSpace($VaultRoot) -or -not [string]::IsNullOrWhiteSpace($ConfigRoot) -or -not [string]::IsNullOrWhiteSpace($PlanPath)

if ($BootstrapAll -and -not [string]::IsNullOrWhiteSpace($BootstrapAdapter)) {
    throw 'Use either -BootstrapAll or -BootstrapAdapter, not both.'
}

if ([string]::IsNullOrWhiteSpace($PackageRoot) -and -not (Test-Path -LiteralPath (Join-Path $defaultPackageRoot 'artifacts.json') -PathType Leaf)) {
    if ($NonInteractive) { throw 'This source installer is not an extracted release package. Pass -PackageRoot with a folder that contains artifacts.json.' }
    Write-InstallerHeader 'Choose the extracted Rule Vault package' 5
    Write-Host '  This script is running from source files, not from an install package.' -ForegroundColor Yellow
    Write-Host '  Select the extracted package folder once; your vault location is chosen next.' -ForegroundColor Gray
    Write-Host "  Suggested package folder: $defaultPackageRoot" -ForegroundColor DarkGray
    Write-Host ''
    while ($true) {
        $candidatePackageRoot = Read-RequiredPath 'Extracted package folder' $defaultPackageRoot
        if (Test-Path -LiteralPath (Join-Path $candidatePackageRoot 'artifacts.json') -PathType Leaf) { $PackageRoot = $candidatePackageRoot; break }
        Write-Host '  That folder is not a Rule Vault install package. Choose the extracted folder containing artifacts.json.' -ForegroundColor Yellow
    }
}

if (-not $NonInteractive -and -not $providedLocations) {
    $locationChoice = Read-LocationMenu
    if ($locationChoice -eq 2) { Complete-InstallerProgress; Write-Host '  No changes were made.' -ForegroundColor Gray; exit 0 }
    if ($locationChoice -eq 1) { $customLocations = Read-CustomLocations; $VaultRoot = $customLocations.VaultRoot }
}

if ([string]::IsNullOrWhiteSpace($PackageRoot)) { $PackageRoot = $defaultPackageRoot }
if ([string]::IsNullOrWhiteSpace($VaultRoot)) { $VaultRoot = $defaultVaultRoot }
if ([string]::IsNullOrWhiteSpace($ConfigRoot)) { $ConfigRoot = $defaultConfigRoot }
$vaultIdWasProvided = -not [string]::IsNullOrWhiteSpace($VaultId)
if ([string]::IsNullOrWhiteSpace($VaultId)) { $VaultId = [guid]::NewGuid().ToString('D') }

$packageRootFull = [IO.Path]::GetFullPath($PackageRoot)
$vaultRootFull = [IO.Path]::GetFullPath($VaultRoot)
$configRootFull = [IO.Path]::GetFullPath($ConfigRoot)
$planPathWasDefault = [string]::IsNullOrWhiteSpace($PlanPath)
if ([string]::IsNullOrWhiteSpace($PlanPath)) { $PlanPath = Join-Path (Join-Path ([IO.Path]::GetTempPath()) 'RuleVault') ("install-plan-$VaultId.json") }
$planPathFull = [IO.Path]::GetFullPath($PlanPath)

Update-InstallerProgress 20 'Checking the selected location'
$existingVault = Test-Path -LiteralPath $vaultRootFull

Update-InstallerProgress 40 'Checking the package manifest'
$manifestPath = Join-Path $packageRootFull 'artifacts.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'artifacts.json is required; do not execute an unverified package.' }

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$packageFiles = @($manifest.package_files)
if ($packageFiles.Count -eq 0) { throw 'Package manifest has no verified package files.' }
$packagePrefix = $packageRootFull.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
foreach ($packageFile in $packageFiles) {
    $relativePath = [string]$packageFile.relative_path
    if ([string]::IsNullOrWhiteSpace($relativePath) -or [IO.Path]::IsPathRooted($relativePath) -or $relativePath.Contains('\') -or $relativePath.Split('/') -contains '..') {
        throw 'Package manifest contains an unsafe relative path.'
    }
    $verifiedPath = [IO.Path]::GetFullPath((Join-Path $packageRootFull $relativePath))
    if (-not $verifiedPath.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $verifiedPath -PathType Leaf)) {
        throw "Verified package file is missing or outside the package: $relativePath"
    }
    if (-not [string]::Equals((Get-FileHash -LiteralPath $verifiedPath -Algorithm SHA256).Hash, [string]$packageFile.raw_sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Package file digest mismatch: $relativePath"
    }
}
$artifact = $manifest.artifacts | Where-Object { $_.rid -eq 'win-x64' } | Select-Object -First 1
if ($null -eq $artifact) { throw 'Package has no verified artifact for win-x64.' }

$artifactPath = Join-Path $packageRootFull $artifact.relative_path
if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) { throw 'Verified artifact path is missing.' }

Update-InstallerProgress 55 'Verifying the Windows CLI artifact'
if (-not [string]::Equals((Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash, $artifact.raw_sha256, [StringComparison]::OrdinalIgnoreCase)) { throw 'Artifact digest mismatch; package execution is blocked.' }
$guidePath = Join-Path $packageRootFull 'guide.md'
if (-not (Test-Path -LiteralPath $guidePath -PathType Leaf)) { throw 'The package installation guide is missing.' }
$guideSha256 = Get-CanonicalTextSha256 $guidePath

$operation = 'Install a new vault'
if ($existingVault) {
    Update-InstallerProgress 60 'Checking the existing vault'
    $inspectionResult = & $artifactPath vault identity --vault-root $vaultRootFull --format json
    if ($LASTEXITCODE -ne 0) { throw 'The existing vault could not be verified. No update was planned.' }
    $inspection = $inspectionResult | Out-String | ConvertFrom-Json
    if ($inspection.code -ne 'OK') { throw "The existing vault could not be verified: $($inspection.summary)" }
    if ($vaultIdWasProvided -and $VaultId -ne $inspection.data.vault_id) { throw 'The supplied vault ID does not match the existing vault. No update was planned.' }

    $VaultId = $inspection.data.vault_id
    if ($planPathWasDefault) { $planPathFull = Join-Path (Join-Path ([IO.Path]::GetTempPath()) 'RuleVault') ("update-plan-$VaultId.json") }
    $operation = 'Update the existing vault'
}

if ($existingVault) {
    $registryManifestState = Get-RegistryManifestState $vaultRootFull $configRootFull $VaultId
    if (-not $registryManifestState.Matches) {
        $mismatch = [pscustomobject]@{ code = 'VAULT_REGISTRY_INTEGRITY_MISMATCH' }
        $script:IntegrityRepairOutcome = 'not-attempted'
        if (-not (Invoke-IntegrityRepair $artifactPath $packageRootFull $vaultRootFull $configRootFull $VaultId 'accept-current' $mismatch)) {
            if ($script:IntegrityRepairOutcome -in @('planned', 'cancelled')) { exit 0 }
            throw 'The saved vault fingerprint could not be repaired.'
        }
    }
}

$planDirectory = Split-Path -Parent $planPathFull
if (-not (Test-Path -LiteralPath $planDirectory)) { New-Item -ItemType Directory -Path $planDirectory -Force | Out-Null }

Update-InstallerProgress 70 'Building a reviewable plan'
if ($existingVault) {
    $planResult = & $artifactPath update plan --vault-root $vaultRootFull --config-root $configRootFull --vault-id $VaultId --package-root $packageRootFull --guide-sha256 $guideSha256 --output $planPathFull --format json
} else {
    $planResult = & $artifactPath install plan --vault-root $vaultRootFull --config-root $configRootFull --vault-id $VaultId --package-root $packageRootFull --guide-sha256 $guideSha256 --output $planPathFull --format json
}
if ($LASTEXITCODE -ne 0) {
    $planExitCode = $LASTEXITCODE
    $failure = $null
    try { $failure = $planResult | Out-String | ConvertFrom-Json } catch { }
    $repairable = $existingVault -and $failure -and $failure.code -in @('INTEGRITY_ANCHOR_MISMATCH', 'INTEGRITY_HASH_MISMATCH')
    $script:IntegrityRepairOutcome = 'not-attempted'
    if ($repairable -and (Invoke-IntegrityRepair $artifactPath $packageRootFull $vaultRootFull $configRootFull $VaultId $IntegrityRepairStrategy $failure)) {
        $planResult = & $artifactPath update plan --vault-root $vaultRootFull --config-root $configRootFull --vault-id $VaultId --package-root $packageRootFull --guide-sha256 $guideSha256 --output $planPathFull --format json
        if ($LASTEXITCODE -ne 0) { Show-PlanBlocked $planResult $LASTEXITCODE; exit $LASTEXITCODE }
    } else {
        if ($repairable -and $script:IntegrityRepairOutcome -in @('planned', 'cancelled')) { exit 0 }
        Show-PlanBlocked $planResult $planExitCode
        exit $planExitCode
    }
}
if ((($planResult | Out-String | ConvertFrom-Json).code) -ne 'OK') { throw 'Plan creation returned an unexpected result.' }

$plan = Get-Content -LiteralPath $planPathFull -Raw | ConvertFrom-Json
$plan | Add-Member -NotePropertyName plan_path -NotePropertyValue $planPathFull -Force
Show-PlainPlan $plan $operation
if (-not (Confirm-PlanChanges)) {
    Complete-InstallerProgress
    if ($NonInteractive) {
        Write-Host '  No files were changed. Run again with -Apply to approve this plan.' -ForegroundColor Yellow
    } else {
        Write-Host '  Okay. Nothing was changed.' -ForegroundColor Gray
    }
    exit 0
}

Update-InstallerProgress 90 'Applying verified files to the selected locations'
$applyResult = & $artifactPath plan apply $planPathFull --approve $plan.plan_sha256 --format json
if ($LASTEXITCODE -ne 0) {
    if ($NonInteractive) { $applyResult | Write-Output; exit $LASTEXITCODE }
    Write-InstallerHeader 'Update needs attention' 90
    Write-Host '  ❌ The update could not finish. The reviewed plan was retained for recovery.' -ForegroundColor Yellow
    Write-Host "     Plan: $planPathFull" -ForegroundColor Cyan
    exit $LASTEXITCODE
}

if ((($applyResult | Out-String | ConvertFrom-Json).code) -eq 'OK') {
    Confirm-InstalledVaultReady $artifactPath $packageRootFull $vaultRootFull $configRootFull $VaultId $guideSha256 $IntegrityRepairStrategy
    if (-not (Get-RegistryManifestState $vaultRootFull $configRootFull $VaultId).Matches) {
        throw 'The installer could not make the saved vault fingerprint match the current integrity manifest. Success was not reported.'
    }
    Update-InstallerProgress 95 'Installing the verified local CLI'
    try { $cliInstall = Install-TrustedCli $artifactPath $artifact.raw_sha256 $configRootFull $guideSha256 }
    catch {
        if ($NonInteractive) { throw }
        Write-InstallerHeader 'Vault installed; CLI setup needs attention' 95
        Write-Host '  The vault plan was applied, but the verified local CLI could not be placed in AppData.' -ForegroundColor Yellow
        Write-Host '  Rerun this installer after resolving the filesystem issue; your vault files were preserved.' -ForegroundColor Gray
        throw
    }
    Confirm-AgentContextReady $cliInstall.CliPath $configRootFull
    if ($BootstrapAll) {
        Update-InstallerProgress 100 'Adding selected agent bootstraps'
        Invoke-RuleVaultBootstrap $cliInstall.CliPath $configRootFull '' $true $BootstrapHomeRoot | Out-Null
    } elseif (-not [string]::IsNullOrWhiteSpace($BootstrapAdapter)) {
        Update-InstallerProgress 100 'Adding the selected agent bootstrap'
        Invoke-RuleVaultBootstrap $cliInstall.CliPath $configRootFull $BootstrapAdapter $false $BootstrapHomeRoot | Out-Null
    } elseif (-not $NonInteractive) {
        Update-InstallerProgress 100 'Adding safe agent bootstraps'
        Invoke-RuleVaultBootstrap $cliInstall.CliPath $configRootFull '' $true $BootstrapHomeRoot | Out-Null
    }
    
    Complete-InstallerProgress
    if (-not $NonInteractive) { Clear-Host; Write-InstallerHeader }
    Write-Host ''
    
    Write-Host "====================================================" -ForegroundColor Green
    if ($existingVault) { Write-Host "  ✔  SUCCESS: Rule Vault is updated." -ForegroundColor Green } else { Write-Host "  ✔  SUCCESS: Rule Vault is installed." -ForegroundColor Green }
    Write-Host "====================================================" -ForegroundColor Green
    Write-Host " 📂 Vault: " -NoNewline -ForegroundColor Gray
    Write-Host "$vaultRootFull" -ForegroundColor White
    Write-Host " 🔒 CLI:   " -NoNewline -ForegroundColor Gray
    Write-Host "$($cliInstall.CliPath)" -ForegroundColor White
    if ($cliInstall.PathAdded) {
        Write-Host " ⌨ Command: " -NoNewline -ForegroundColor Gray
        Write-Host 'rulevault (open a new terminal before using it)' -ForegroundColor White
    } else {
        Write-Host " ⌨ Command: " -NoNewline -ForegroundColor Gray
        Write-Host 'rulevault' -ForegroundColor White
    }
    Write-Host " 🗒 Agent descriptor: " -NoNewline -ForegroundColor Gray
    Write-Host "$($cliInstall.DescriptorPath)" -ForegroundColor DarkGray
    Write-Host "====================================================" -ForegroundColor Green
}
exit $LASTEXITCODE
