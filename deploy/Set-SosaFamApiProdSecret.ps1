<#
.SYNOPSIS
    Saves one production secret for sosa-fam-api as an environment variable on its IIS app pool.

.DESCRIPTION
    You run this yourself, never Claude. It asks for the value at a hidden prompt (or generates a
    random one with -Generate), sends it over PowerShell remoting (Kerberos-encrypted) to the API
    server, writes it into the app pool's environmentVariables in applicationHost.config through
    Microsoft.Web.Administration (in-process: no appcmd command line), and recycles the pool.
    The value never goes into the repository, the published files, a log, or chat.

    Secrets the API reads:
      ConnectionStrings__SosaFam   Server=SOLITMDB001;Database=SOS_FAMILY;User Id=sosa-fam-api;Password=<password>;TrustServerCertificate=True
      Jwt__SigningKey              random, at least 32 bytes: use -Generate

.EXAMPLE
    .\deploy\Set-SosaFamApiProdSecret.ps1 -Name ConnectionStrings__SosaFam
.EXAMPLE
    .\deploy\Set-SosaFamApiProdSecret.ps1 -Name Jwt__SigningKey -Generate
.EXAMPLE
    .\deploy\Set-SosaFamApiProdSecret.ps1 -List
#>
[CmdletBinding(DefaultParameterSetName = 'Set')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Set')]
    [Parameter(Mandatory, ParameterSetName = 'Remove')]
    [ValidatePattern('^[A-Za-z0-9_]+$')]
    [string]$Name,

    [Parameter(ParameterSetName = 'Set')][switch]$Generate,
    [Parameter(ParameterSetName = 'Remove')][switch]$Remove,
    [Parameter(ParameterSetName = 'List')][switch]$List,

    [string]$Server = 'QUADITMAPP001',
    [string]$AppPool = 'sosa-fam-api.nuvol9.com',
    [switch]$NoRecycle
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$value = $null
if ($PSCmdlet.ParameterSetName -eq 'Set') {
    if ($Generate) {
        $bytes = New-Object byte[] 64
        [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        $value = [Convert]::ToBase64String($bytes)
        Write-Host "Generated a random 64-byte value for $Name (not shown)."
    } else {
        $secure = Read-Host "Value for $Name on $Server/$AppPool (typing is hidden)" -AsSecureString
        $value = [System.Net.NetworkCredential]::new('', $secure).Password
        if ([string]::IsNullOrWhiteSpace($value)) {
            Write-Host 'Canceled. Nothing saved.'
            exit 1
        }
    }
}

$session = New-PSSession -ComputerName $Server
try {
    $result = Invoke-Command -Session $session -ArgumentList $PSCmdlet.ParameterSetName, $Name, $value, $AppPool, [bool]$NoRecycle -ScriptBlock {
        param($Mode, $Name, $Value, $AppPool, $NoRecycle)
        $ErrorActionPreference = 'Stop'
        Add-Type -Path "$env:windir\System32\inetsrv\Microsoft.Web.Administration.dll"
        $manager = New-Object Microsoft.Web.Administration.ServerManager
        try {
            $pools = $manager.GetApplicationHostConfiguration().GetSection('system.applicationHost/applicationPools').GetCollection()
            $pool = $pools | Where-Object { $_['name'] -eq $AppPool }
            if (-not $pool) { throw "App pool '$AppPool' not found on $env:COMPUTERNAME." }
            $variables = $pool.GetCollection('environmentVariables')

            if ($Mode -eq 'List') {
                return @($variables | ForEach-Object { $_['name'] })
            }

            $existing = $variables | Where-Object { $_['name'] -eq $Name }
            if ($existing) { $variables.Remove($existing) }
            if ($Mode -eq 'Set') {
                $element = $variables.CreateElement('add')
                $element['name'] = $Name
                $element['value'] = $Value
                $variables.Add($element)
            } elseif (-not $existing) {
                return "Nothing to remove: $Name is not set on $AppPool."
            }
            $manager.CommitChanges()
            if (-not $NoRecycle) { $manager.ApplicationPools[$AppPool].Recycle() | Out-Null }
            $verb = if ($Mode -eq 'Set') { 'Saved' } else { 'Removed' }
            "$verb $Name on $AppPool$(if (-not $NoRecycle) { ' and recycled the pool' })."
        } finally {
            $manager.Dispose()
        }
    }
    if ($List) {
        Write-Host "Environment variables on $Server/$AppPool (names only):"
        $result | ForEach-Object { Write-Host "  $_" }
    } else {
        Write-Host $result
    }
} finally {
    Remove-PSSession $session
    $value = $null
}
