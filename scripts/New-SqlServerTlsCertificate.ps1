# Copyright (c) clast-project. All rights reserved.
# Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

<#
.SYNOPSIS
    Creates a self-signed TLS certificate for a local SQL Server instance and
    configures the instance to use it.

.DESCRIPTION
    Strict encryption (TDS 8, adbc.mssql.encrypt=strict) needs the server to
    have a certificate configured; without one, SQL Server refuses strict
    connections with "A valid TLS certificate is not configured to accept
    strict (TDS 8.0 and above) connections". This script, for a development
    machine:

      1. creates a certificate in LocalMachine\My for this computer's names
         (FQDN, computer name, localhost), with a server-authentication EKU
         and an RSA key in the legacy SChannel provider, which SQL Server
         accepts;
      2. grants the SQL Server service account read access to the private key;
      3. selects it for the instance (SuperSocketNetLib\Certificate);
      4. optionally (-Trust) adds it to LocalMachine\Root, so clients can
         validate it without trust_server_certificate;
      5. restarts the instance and the services that depend on it.

    It does not force encryption or strict encryption for all clients.
    Run it from an elevated PowerShell. Undo with -Remove.

.EXAMPLE
    .\New-SqlServerTlsCertificate.ps1 -Trust

.EXAMPLE
    .\New-SqlServerTlsCertificate.ps1 -Remove
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    # The instance name; MSSQLSERVER is the default instance.
    [string] $InstanceName = 'MSSQLSERVER',

    # Names the certificate is valid for; defaults to the FQDN, computer name and localhost.
    [string[]] $DnsName,

    [int] $ValidYears = 2,

    # Also trust the certificate machine-wide (LocalMachine\Root).
    [switch] $Trust,

    # Configure without restarting SQL Server (the certificate takes effect at the next restart).
    [switch] $NoRestart,

    # Undo: clear the instance's certificate setting and delete certificates this script created.
    [switch] $Remove
)

#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'

$friendlyName = "SQL Server TLS ($InstanceName, created by New-SqlServerTlsCertificate)"

$instanceId = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL' -ErrorAction SilentlyContinue).$InstanceName
if (-not $instanceId) {
    throw "SQL Server instance '$InstanceName' is not installed on this machine."
}
$netLibKey = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\$instanceId\MSSQLServer\SuperSocketNetLib"
$serviceName = if ($InstanceName -eq 'MSSQLSERVER') { 'MSSQLSERVER' } else { "MSSQL`$$InstanceName" }
$service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
if (-not $service) {
    throw "Service '$serviceName' not found."
}
$serviceAccount = $service.StartName
Write-Host "Instance $InstanceName ($instanceId), service $serviceName running as $serviceAccount"

function Restart-SqlInstance {
    if ($NoRestart) {
        Write-Warning "Restart $serviceName for the change to take effect."
        return
    }
    # Restart-Service stops dependent services but does not start them again.
    $dependents = Get-Service -Name $serviceName -DependentServices | Where-Object Status -eq 'Running'
    if ($PSCmdlet.ShouldProcess($serviceName, 'Restart service')) {
        Restart-Service -Name $serviceName -Force
        foreach ($dependent in $dependents) {
            Start-Service -Name $dependent.Name
        }
        Write-Host "Restarted $serviceName$(if ($dependents) { ' and ' + ($dependents.Name -join ', ') })."
    }
}

if ($Remove) {
    if ($PSCmdlet.ShouldProcess($netLibKey, 'Clear Certificate')) {
        Set-ItemProperty -Path $netLibKey -Name Certificate -Value ''
    }
    foreach ($store in 'Cert:\LocalMachine\My', 'Cert:\LocalMachine\Root') {
        Get-ChildItem $store | Where-Object FriendlyName -eq $friendlyName | ForEach-Object {
            if ($PSCmdlet.ShouldProcess("$store\$($_.Thumbprint)", 'Delete certificate')) {
                Remove-Item -Path "$store\$($_.Thumbprint)" -DeleteKey -ErrorAction SilentlyContinue
                if (Test-Path "$store\$($_.Thumbprint)") { Remove-Item -Path "$store\$($_.Thumbprint)" }
                Write-Host "Deleted $($_.Thumbprint) from $store"
            }
        }
    }
    Restart-SqlInstance
    return
}

$previous = (Get-ItemProperty -Path $netLibKey -Name Certificate -ErrorAction SilentlyContinue).Certificate
if ($previous) {
    Write-Warning "The instance already uses certificate $previous; it will be replaced (the old certificate is left in the store)."
}

if (-not $DnsName) {
    $fqdn = [System.Net.Dns]::GetHostEntry($env:COMPUTERNAME).HostName
    $DnsName = @($fqdn, $env:COMPUTERNAME, 'localhost') | Select-Object -Unique
}
Write-Host "Creating a certificate for: $($DnsName -join ', ')"

# Reuse a certificate an earlier (perhaps interrupted) run created, if it is still valid for a while.
$certificate = Get-ChildItem Cert:\LocalMachine\My |
    Where-Object { $_.FriendlyName -eq $friendlyName -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if ($certificate) {
    Write-Host "Reusing certificate $($certificate.Thumbprint) from an earlier run, valid until $($certificate.NotAfter)"
}
elseif (-not $PSCmdlet.ShouldProcess('Cert:\LocalMachine\My', 'Create certificate')) {
    return
}
else {
    $certificate = New-SelfSignedCertificate `
        -Type SSLServerAuthentication `
        -Subject "CN=$($DnsName[0])" `
        -DnsName $DnsName `
        -CertStoreLocation Cert:\LocalMachine\My `
        -FriendlyName $friendlyName `
        -KeyAlgorithm RSA -KeyLength 2048 `
        -KeySpec KeyExchange `
        -Provider 'Microsoft RSA SChannel Cryptographic Provider' `
        -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddYears($ValidYears)
    Write-Host "Created certificate $($certificate.Thumbprint), valid until $($certificate.NotAfter)"
}

# Let the service account read the private key. Windows PowerShell returns the key as
# RSACryptoServiceProvider; PowerShell 7 as RSACng even for a key in a legacy (CAPI)
# provider. Either way the unique name is the key file's name, in one of the machine
# key folders: RSA\MachineKeys for CAPI keys, Keys for CNG keys.
$rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
$keyName = if ($rsa -is [System.Security.Cryptography.RSACryptoServiceProvider]) {
    $rsa.CspKeyContainerInfo.UniqueKeyContainerName
}
elseif ($rsa -is [System.Security.Cryptography.RSACng]) {
    $rsa.Key.UniqueName
}
else {
    throw "Unexpected private key type $($rsa.GetType().FullName)."
}
$keyFile = 'Microsoft\Crypto\RSA\MachineKeys', 'Microsoft\Crypto\Keys', 'Microsoft\Crypto\SystemKeys' |
    ForEach-Object { Join-Path $env:ProgramData "$_\$keyName" } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1
if (-not $keyFile) {
    throw "Private key file '$keyName' not found under $env:ProgramData\Microsoft\Crypto."
}
& icacls.exe $keyFile /grant "${serviceAccount}:R" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "icacls failed to grant $serviceAccount access to $keyFile."
}
Write-Host "Granted $serviceAccount read access to the private key."

# SQL Server expects the thumbprint in lower case.
Set-ItemProperty -Path $netLibKey -Name Certificate -Value $certificate.Thumbprint.ToLowerInvariant()
Write-Host "Configured $InstanceName to use the certificate."

if ($Trust) {
    $publicOnly = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificate.Export('Cert'))
    $root = [System.Security.Cryptography.X509Certificates.X509Store]::new('Root', 'LocalMachine')
    $root.Open('ReadWrite')
    try {
        $publicOnly.FriendlyName = $friendlyName
        $root.Add($publicOnly)
    }
    finally {
        $root.Close()
    }
    Write-Host "Added the certificate to LocalMachine\Root (trusted on this machine)."
}

Restart-SqlInstance

Write-Host ""
Write-Host "Done. Strict connections: adbc.mssql.encrypt=strict$(if (-not $Trust) { ' with adbc.mssql.trust_server_certificate=true' })."
Write-Host "To undo: .\New-SqlServerTlsCertificate.ps1 -Remove"
