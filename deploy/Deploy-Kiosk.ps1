# Instalacja / aktualizacja kasy na stanowisku – uruchamiane przez agenta Azure DevOps (pipeline) albo ręcznie jako administrator.
#
# Pierwsza instalacja (brak usługi): kopiuje aplikację, tworzy appsettings.Local.json z pliku wspólnych ustawień
# (-LocalSettingsFile), zakłada usługę Windows i autostart przeglądarki w trybie kiosku.
# Aktualizacja: czeka, aż nikt nie robi zakupów (kasa sama pokazuje "Trwa aktualizacja"), zatrzymuje usługę i podmienia
# wyłącznie pliki aplikacji – appsettings*.json, data\ (ustawienia stanowiska, dziennik transakcji) i logs\ zostają.
# Nowa wersja, która nie wystartuje, jest wycofywana do poprzedniej. Ta sama wersja = nic do zrobienia.
#
# Windows PowerShell 5.1.
param(
    [Parameter(Mandatory = $true)][string]$PackageDir,
    [string]$InstallDir = "C:\GaskaSelfCheckout",
    [string]$ServiceName = "GaskaKasaSCO",
    [string]$DisplayName = "Kasa samoobslugowa",
    [int]$Port = 5080,
    [string]$LocalSettingsFile = "",
    [string]$ServiceUser = "",
    [string]$ServicePassword = "",
    [int]$DrainTimeoutMinutes = 30,
    [int]$StartTimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
# a pipeline variable that is not defined arrives as its literal name, e.g. "$(ServiceUser)"
if ($ServiceUser -like '$(*') { $ServiceUser = "" }
if ($ServicePassword -like '$(*') { $ServicePassword = "" }
$exeName = "Gaska.SelfCheckout.Web.exe"
$appDll = "Gaska.SelfCheckout.Web.dll"
$baseUrl = "http://localhost:$Port/maintenance"
$backupDir = "$InstallDir.previous"
# never replaced or removed by an update
$keepFiles = @("appsettings.json", "appsettings.*.json")
$keepDirs = @("data", "logs")

function Write-Step([string]$text) { Write-Host "==> $text" }

function Get-FileVersion([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    return (Get-Item $path).VersionInfo.ProductVersion -replace '\+.*$', ''
}

function Invoke-Maintenance([string]$method) {
    try { return Invoke-RestMethod -Uri $baseUrl -Method $method -TimeoutSec 10 -UseBasicParsing }
    catch { return $null }
}

# robocopy: exit codes 0-7 = success
function Copy-App([string]$from, [string]$to, [switch]$All) {
    # excluded files and folders are neither copied nor removed by /MIR
    $roboArgs = @($from, $to, "/MIR", "/R:3", "/W:2", "/NFL", "/NDL", "/NJH", "/NP")
    if (-not $All) { $roboArgs += @("/XF") + $keepFiles + @("/XD") + $keepDirs }
    & robocopy @roboArgs | Out-Host
    if ($LASTEXITCODE -ge 8) { throw "robocopy $from -> $to failed ($LASTEXITCODE)" }
    $global:LASTEXITCODE = 0
}

function Stop-Kiosk {
    $service = Get-Service $ServiceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne "Stopped") {
        Stop-Service $ServiceName -Force
        $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(60))
    }
    # the executable may stay locked for a moment after the service stops
    $exe = Join-Path $InstallDir $exeName
    for ($i = 0; $i -lt 30 -and (Get-Process | Where-Object { $_.Path -eq $exe }); $i++) { Start-Sleep -Seconds 1 }
}

function Start-Kiosk([string]$expectedVersion) {
    Start-Service $ServiceName
    $deadline = (Get-Date).AddSeconds($StartTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $state = Invoke-Maintenance "GET"
        if ($state -and $state.version -eq $expectedVersion) { return $true }
        Start-Sleep -Seconds 3
    }
    return $false
}

# "Log on as a service" (SeServiceLogonRight) – sc.exe config obj= does not grant it, without it the service cannot start
function Grant-ServiceLogonRight([string]$account) {
    if (-not ("ServiceLogonRight" -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Security.Principal;

public static class ServiceLogonRight
{
    [StructLayout(LayoutKind.Sequential)]
    struct LsaUnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    struct LsaObjectAttributes { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }

    [DllImport("advapi32.dll")] static extern uint LsaOpenPolicy(IntPtr systemName, ref LsaObjectAttributes attributes, uint access, out IntPtr policy);
    [DllImport("advapi32.dll")] static extern uint LsaAddAccountRights(IntPtr policy, byte[] sid, LsaUnicodeString[] rights, uint count);
    [DllImport("advapi32.dll")] static extern uint LsaClose(IntPtr policy);
    [DllImport("advapi32.dll")] static extern int LsaNtStatusToWinError(uint status);

    public static void Grant(string account)
    {
        var sid = (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
        var sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);

        var attributes = new LsaObjectAttributes();
        IntPtr policy;
        uint status = LsaOpenPolicy(IntPtr.Zero, ref attributes, 0x00000800 | 0x00000010, out policy); // CREATE_ACCOUNT | LOOKUP_NAMES
        if (status != 0) throw new System.ComponentModel.Win32Exception(LsaNtStatusToWinError(status));
        try
        {
            const string right = "SeServiceLogonRight";
            var name = new LsaUnicodeString { Buffer = Marshal.StringToHGlobalUni(right), Length = (ushort)(right.Length * 2), MaximumLength = (ushort)((right.Length + 1) * 2) };
            try
            {
                status = LsaAddAccountRights(policy, sidBytes, new[] { name }, 1);
                if (status != 0) throw new System.ComponentModel.Win32Exception(LsaNtStatusToWinError(status));
            }
            finally { Marshal.FreeHGlobal(name.Buffer); }
        }
        finally { LsaClose(policy); }
    }
}
"@
    }
    [ServiceLogonRight]::Grant($account)
}

# an existing installation is updated where it is (e.g. installed manually in another folder)
$existing = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
if ($existing -and $existing.PathName) {
    $InstallDir = Split-Path ($existing.PathName.Trim('"') -replace '\.exe".*$', '.exe') -Parent
    $backupDir = "$InstallDir.previous"
}

# the application is framework-dependent and 32-bit
if (-not (Get-ChildItem "${env:ProgramFiles(x86)}\dotnet\shared\Microsoft.AspNetCore.App" -Directory -Filter "10.*" -ErrorAction SilentlyContinue)) {
    throw "ASP.NET Core Runtime 10 (x86) is not installed on this workstation"
}

$packageVersion = Get-FileVersion (Join-Path $PackageDir $appDll)
if (-not $packageVersion) { throw "No $appDll in $PackageDir" }
$installedVersion = Get-FileVersion (Join-Path $InstallDir $appDll)
$service = Get-Service $ServiceName -ErrorAction SilentlyContinue
Write-Step "Package $packageVersion, installed $(if ($installedVersion) { $installedVersion } else { '-' }), service $(if ($service) { $service.Status } else { 'missing' })"

# also repairs a service installed before (idempotent)
if ($ServiceUser) {
    Write-Step "Granting 'Log on as a service' to $ServiceUser"
    Grant-ServiceLogonRight $ServiceUser
    # the account / password in the variable group may have changed since the installation
    if ($service) {
        & sc.exe config $ServiceName obj= $ServiceUser password= $ServicePassword | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Service account could not be set" }
        & icacls $InstallDir /grant "${ServiceUser}:(OI)(CI)M" /T /Q | Out-Null
    }
}

# ---------------------------------------------------------------- first installation
if (-not $service) {
    Write-Step "First installation in $InstallDir"
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-App $PackageDir $InstallDir -All
    $local = Join-Path $InstallDir "appsettings.Local.json"
    if (-not (Test-Path $local)) {
        if ($LocalSettingsFile -and (Test-Path $LocalSettingsFile)) { Copy-Item $LocalSettingsFile $local }
        else { Write-Warning "No appsettings.Local.json - fill in the connection settings before the kiosk can sell" }
    }

    $exe = Join-Path $InstallDir $exeName
    & sc.exe create $ServiceName binPath= "`"$exe`"" start= delayed-auto DisplayName= $DisplayName | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Service could not be created" }
    if ($ServiceUser) {
        & sc.exe config $ServiceName obj= $ServiceUser password= $ServicePassword | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Service account could not be set" }
    } else {
        Write-Warning "No service account - the service runs as LocalSystem (XL needs the account the XL database is registered for)"
    }
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Host

    # the account running the service writes data\ and logs\
    if ($ServiceUser) { & icacls $InstallDir /grant "${ServiceUser}:(OI)(CI)M" /T /Q | Out-Null }

    # kiosk browser for every user signing in to the workstation
    $kioskCmd = Join-Path $InstallDir "kiosk.cmd"
    $startup = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\StartUp\Kasa samoobslugowa.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($startup)
    $shortcut.TargetPath = $kioskCmd
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.WindowStyle = 7
    $shortcut.Save()

    if (-not (Start-Kiosk $packageVersion)) { throw "The kiosk did not start - see $InstallDir\logs" }
    Write-Step "Installed $packageVersion. Set the workstation (terminal, printer, warehouses) on http://localhost:$Port/settings"
    exit 0
}

# ---------------------------------------------------------------- update
if ($installedVersion -eq $packageVersion) {
    Write-Step "Already up to date"
    if ((Get-Service $ServiceName).Status -ne "Running") { Start-Service $ServiceName }
    exit 0
}

# wait for an idle kiosk: customers finish shopping and paying first
if ((Get-Service $ServiceName).Status -eq "Running") {
    Write-Step "Waiting for the kiosk to be idle (at most $DrainTimeoutMinutes min)"
    $state = Invoke-RestMethod -Uri "$($baseUrl)?minutes=$($DrainTimeoutMinutes + 5)" -Method Post -TimeoutSec 15 -UseBasicParsing
    $deadline = (Get-Date).AddMinutes($DrainTimeoutMinutes)
    while (-not $state.ready) {
        if ((Get-Date) -gt $deadline) {
            Invoke-Maintenance "DELETE" | Out-Null
            throw "The kiosk was busy for $DrainTimeoutMinutes min - update skipped, run the deployment again later"
        }
        Start-Sleep -Seconds 5
        $state = Invoke-Maintenance "GET"
        if (-not $state) { throw "The kiosk stopped answering while waiting - update skipped" }
    }
}

Write-Step "Kiosk idle - updating"
Stop-Kiosk
Copy-App $InstallDir $backupDir
Copy-App $PackageDir $InstallDir
# repair the service path of older installations
& sc.exe config $ServiceName binPath= "`"$(Join-Path $InstallDir $exeName)`"" | Out-Null

if (Start-Kiosk $packageVersion) {
    Write-Step "Updated to $packageVersion"
    exit 0
}

Write-Warning "Version $packageVersion did not start - restoring $installedVersion"
Stop-Kiosk
Copy-App $backupDir $InstallDir
Start-Service $ServiceName
throw "Update to $packageVersion failed and was rolled back - see $InstallDir\logs"
