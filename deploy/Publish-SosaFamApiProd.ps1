<#
.SYNOPSIS
    Publishes sosa-fam-api to production IIS on QUADITMAPP001.

.DESCRIPTION
    1. Checks the checkout: on main, in sync with origin/main, nothing uncommitted.
    2. Runs the tests (dotnet test) unless -SkipTests.
    3. dotnet publish -c Release into a temporary folder and zips it.
    4. Opens a PowerShell remoting session with your own Windows sign-in (no stored credential).
    5. On the server: puts app_offline.htm in the site folder, backs up the current files to
       <BackupRoot>\<timestamp> (keeps the newest -Keep), copies the new files in (logs\ and
       app_offline.htm are kept), makes sure logs\ is writable by the app pool, removes
       app_offline.htm and recycles the pool.
    6. Warm-up: calls http://localhost:<LocalPort>/health on the server and fails unless it answers 200.

    No secret is needed or handled here. The API reads ConnectionStrings__SosaFam and
    Jwt__SigningKey from its app pool's environment (see Set-SosaFamApiProdSecret.ps1).

    Production guard: -WhatIf shows what would happen and changes nothing. The real run asks you
    to type <Server>/<SiteName>, or takes it from -ConfirmTarget.

.EXAMPLE
    .\deploy\Publish-SosaFamApiProd.ps1 -WhatIf
.EXAMPLE
    .\deploy\Publish-SosaFamApiProd.ps1 -ConfirmTarget 'QUADITMAPP001/sosa-fam-api.nuvol9.com'
.EXAMPLE
    .\deploy\Publish-SosaFamApiProd.ps1 -InitializeSite -WhatIf     # first time: creates the app pool, site and folders
.EXAMPLE
    .\deploy\Publish-SosaFamApiProd.ps1 -Rollback                  # puts the newest backup back
#>
[CmdletBinding()]
param(
    [string]$Server = 'QUADITMAPP001',
    [string]$SiteName = 'sosa-fam-api.nuvol9.com',
    [string]$AppPool = 'sosa-fam-api.nuvol9.com',
    [string]$SitePath = 'D:\Inetpub\sosa-fam-api.nuvol9.com',
    [string]$BackupRoot = 'D:\Inetpub\_backups\sosa-fam-api.nuvol9.com',
    [int]$LocalPort = 7170,
    [ValidateRange(1, 50)][int]$Keep = 5,
    [switch]$InitializeSite,
    [switch]$Rollback,
    [switch]$SkipTests,
    [string]$ConfirmTarget,
    [switch]$WhatIf
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repoRoot 'src/SosaFam.Api/SosaFam.Api.csproj'
$target = "$Server/$SiteName"

function Confirm-Target {
    if ($WhatIf) { return }
    $typed = if ($ConfirmTarget) { $ConfirmTarget } else { Read-Host "Type $target to continue on production" }
    if ($typed -ne $target) { throw "Target not confirmed ('$typed' is not '$target'). Nothing changed." }
}

# Runs on the server: the swap and the rollback share these helpers.
$serverHelpers = {
    function Set-LogsAcl([string]$SitePath, [string]$AppPool) {
        $logs = Join-Path $SitePath 'logs'
        New-Item -ItemType Directory -Force -Path $logs | Out-Null
        $acl = Get-Acl $logs
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule("IIS AppPool\$AppPool", 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.SetAccessRule($rule)
        Set-Acl -Path $logs -AclObject $acl
    }
    function Write-AppOffline([string]$SitePath) {
        Set-Content -Path (Join-Path $SitePath 'app_offline.htm') -Value '<html><body><h1>Updating. Back in a moment.</h1></body></html>' -Encoding UTF8
        Start-Sleep -Seconds 3
    }
    function Copy-SiteFiles([string]$From, [string]$To) {
        # Mirror everything except logs\ and app_offline.htm.
        & robocopy $From $To /MIR /XD (Join-Path $To 'logs') /XF app_offline.htm /R:3 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE." }
    }
    function Restart-Pool([string]$AppPool) {
        Import-Module WebAdministration
        if ((Get-WebAppPoolState -Name $AppPool).Value -eq 'Started') { Restart-WebAppPool -Name $AppPool } else { Start-WebAppPool -Name $AppPool }
    }
    function Test-Health([int]$LocalPort) {
        $url = "http://localhost:$LocalPort/health"
        for ($i = 1; $i -le 10; $i++) {
            try {
                $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 15
                if ($response.StatusCode -eq 200) { return "Warm-up OK: $url answered 200." }
            } catch { Start-Sleep -Seconds 3 }
        }
        throw "Warm-up failed: $url did not answer 200. Check the site's logs folder and the Windows Application event log. To go back: -Rollback."
    }
}

Write-Host "Target: $target   folder: $SitePath   local port: $LocalPort"

# --- Rollback ---------------------------------------------------------------------------------
if ($Rollback) {
    Confirm-Target
    $session = New-PSSession -ComputerName $Server
    try {
        Invoke-Command -Session $session -ScriptBlock $serverHelpers
        Invoke-Command -Session $session -ArgumentList $SitePath, $BackupRoot, $AppPool, $LocalPort, ([bool]$WhatIf) -ScriptBlock {
            param($SitePath, $BackupRoot, $AppPool, $LocalPort, $WhatIf)
            $ErrorActionPreference = 'Stop'
            $latest = Get-ChildItem $BackupRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
            if (-not $latest) { throw "No backup in $BackupRoot." }
            if ($WhatIf) { "Would restore $($latest.FullName) into $SitePath and recycle $AppPool."; return }
            Write-AppOffline $SitePath
            Copy-SiteFiles $latest.FullName $SitePath
            Remove-Item (Join-Path $SitePath 'app_offline.htm') -Force
            Restart-Pool $AppPool
            "Restored $($latest.Name)."
            Test-Health $LocalPort
        } | ForEach-Object { Write-Host $_ }
    } finally { Remove-PSSession $session }
    return
}

# --- Checks on this PC ------------------------------------------------------------------------
Push-Location $repoRoot
try {
    & git fetch --quiet origin main
    $branch = (& git rev-parse --abbrev-ref HEAD).Trim()
    $dirty = & git status --porcelain --untracked-files=no
    $behindAhead = (& git rev-list --left-right --count 'origin/main...HEAD').Trim()
    if ($branch -ne 'main') { throw "Publish from main (this checkout is on '$branch')." }
    if ($dirty) { throw 'There are uncommitted changes. Commit or discard them first.' }
    if ($behindAhead -ne "0`t0" -and $behindAhead -ne '0 0') { throw "main is not in sync with origin/main (behind/ahead: $behindAhead). Pull first." }
    $commit = (& git rev-parse --short HEAD).Trim()
    Write-Host "Source: main at $commit, in sync with origin/main."
} finally { Pop-Location }

if (-not $SkipTests) {
    Write-Host 'Running tests...'
    & dotnet test (Join-Path $repoRoot 'SosaFam.Api.sln') -c Release --nologo -v quiet | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit code $LASTEXITCODE). Nothing published." }
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) "sosafam-api-publish-$([guid]::NewGuid().ToString('N'))"
$out = Join-Path $work 'site'
$zip = Join-Path $work 'site.zip'
try {
    Write-Host 'Publishing...'
    & dotnet publish $project -c Release -o $out --nologo -v quiet | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
    Write-Host ("Package: {0:N1} MB" -f ((Get-Item $zip).Length / 1MB))

    $session = New-PSSession -ComputerName $Server
    try {
        Invoke-Command -Session $session -ScriptBlock $serverHelpers

        # One-time setup: app pool (no managed code, always running), site on the local port, folders.
        if ($InitializeSite) {
            Confirm-Target
            Invoke-Command -Session $session -ArgumentList $SiteName, $AppPool, $SitePath, $BackupRoot, $LocalPort, ([bool]$WhatIf) -ScriptBlock {
                param($SiteName, $AppPool, $SitePath, $BackupRoot, $LocalPort, $WhatIf)
                $ErrorActionPreference = 'Stop'
                Import-Module WebAdministration
                $poolExists = Test-Path "IIS:\AppPools\$AppPool"
                $siteExists = Test-Path "IIS:\Sites\$SiteName"
                $portUsed = Get-WebBinding | Where-Object { $_.bindingInformation -like "*:$($LocalPort):*" }
                if ($WhatIf) {
                    "App pool $AppPool : $(if ($poolExists) { 'exists, unchanged' } else { 'would be created (No Managed Code, AlwaysRunning)' })"
                    "Site $SiteName : $(if ($siteExists) { 'exists, unchanged' } else { "would be created on http port $LocalPort, folder $SitePath" })"
                    if ($portUsed -and -not $siteExists) { "WARNING: port $LocalPort is already bound by another site." }
                    return
                }
                if ($portUsed -and -not $siteExists) { throw "Port $LocalPort is already bound by another site." }
                New-Item -ItemType Directory -Force -Path $SitePath, $BackupRoot | Out-Null
                if (-not $poolExists) {
                    New-WebAppPool -Name $AppPool | Out-Null
                    Set-ItemProperty "IIS:\AppPools\$AppPool" -Name managedRuntimeVersion -Value ''
                    Set-ItemProperty "IIS:\AppPools\$AppPool" -Name startMode -Value 'AlwaysRunning'
                    "Created app pool $AppPool."
                }
                if (-not $siteExists) {
                    New-Website -Name $SiteName -PhysicalPath $SitePath -ApplicationPool $AppPool -Port $LocalPort | Out-Null
                    "Created site $SiteName on port $LocalPort."
                }
                Set-LogsAcl $SitePath $AppPool
            } | ForEach-Object { Write-Host $_ }
            if ($WhatIf) { Write-Host 'Preview only. Nothing changed.' }
            return
        }

        $state = Invoke-Command -Session $session -ArgumentList $SiteName, $AppPool, $SitePath -ScriptBlock {
            param($SiteName, $AppPool, $SitePath)
            Import-Module WebAdministration
            $names = @()
            if (Test-Path "IIS:\AppPools\$AppPool") {
                $pool = Get-Item "IIS:\AppPools\$AppPool"
                $names = @($pool.environmentVariables.Collection | ForEach-Object { $_.name })
            }
            [pscustomobject]@{
                Site    = Test-Path "IIS:\Sites\$SiteName"
                Pool    = Test-Path "IIS:\AppPools\$AppPool"
                Folder  = Test-Path $SitePath
                Secrets = $names
                # The module's DLL, not a registry key (that key often has no subkeys even when installed).
                Hosting = Test-Path (Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll')
            }
        }
        if (-not $state.Site -or -not $state.Pool) { throw "Site or app pool missing on $Server. Run once with -InitializeSite." }
        if (-not $state.Hosting) { Write-Warning 'The ASP.NET Core Hosting Bundle (ANCM V2) was not found on the server. Install the .NET 8 Hosting Bundle.' }
        $missing = @('ConnectionStrings__SosaFam', 'Jwt__SigningKey') | Where-Object { $state.Secrets -notcontains $_ }
        if ($missing) { throw "Missing on app pool $AppPool : $($missing -join ', '). Run .\deploy\Set-SosaFamApiProdSecret.ps1 -Name <name> first." }
        Write-Host 'Server: site, app pool and both secrets are present.'

        if ($WhatIf) {
            Write-Host "Would: app_offline.htm, back up $SitePath to $BackupRoot\<timestamp> (keep $Keep), copy the new files, recycle $AppPool, warm up http://localhost:$LocalPort/health."
            Write-Host 'Preview only. Nothing changed.'
            return
        }

        Confirm-Target
        $remoteZip = Invoke-Command -Session $session -ScriptBlock { Join-Path $env:TEMP "sosafam-api-$([guid]::NewGuid().ToString('N')).zip" }
        Copy-Item -Path $zip -Destination $remoteZip -ToSession $session

        Invoke-Command -Session $session -ArgumentList $remoteZip, $SitePath, $BackupRoot, $AppPool, $LocalPort, $Keep -ScriptBlock {
            param($RemoteZip, $SitePath, $BackupRoot, $AppPool, $LocalPort, $Keep)
            $ErrorActionPreference = 'Stop'
            $staging = Join-Path $env:TEMP "sosafam-api-staging-$([guid]::NewGuid().ToString('N'))"
            try {
                Expand-Archive -Path $RemoteZip -DestinationPath $staging
                Write-AppOffline $SitePath

                $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
                $backup = Join-Path $BackupRoot $stamp
                New-Item -ItemType Directory -Force -Path $backup | Out-Null
                & robocopy $SitePath $backup /E /XD (Join-Path $SitePath 'logs') /XF app_offline.htm /R:3 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
                if ($LASTEXITCODE -ge 8) { throw "Backup failed (robocopy exit code $LASTEXITCODE). The old version is still in place behind app_offline.htm." }
                "Backed up to $backup."
                Get-ChildItem $BackupRoot -Directory | Sort-Object Name -Descending | Select-Object -Skip $Keep | Remove-Item -Recurse -Force

                Copy-SiteFiles $staging $SitePath
                Set-LogsAcl $SitePath $AppPool
                Remove-Item (Join-Path $SitePath 'app_offline.htm') -Force
                Restart-Pool $AppPool
                'New files in place; app pool recycled.'
                Test-Health $LocalPort
            } finally {
                Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item $RemoteZip -Force -ErrorAction SilentlyContinue
            }
        } | ForEach-Object { Write-Host $_ }
        Write-Host "Done: $target is running main at $commit." -ForegroundColor Green
    } finally { Remove-PSSession $session }
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
