# Instalacja kasy jako usługi Windows (uruchomić jako administrator).
# Usługa powinna działać na koncie użytkownika, który ma zarejestrowaną bazę XL i konfigurację drukarki fiskalnej XL.
# Gdy usługa już istnieje (np. ze starszej wersji – Gaska.Kasa.Web.exe), skrypt tylko przestawia ją na bieżący plik.
param([string]$User = "", [string]$Password = "")
$ErrorActionPreference = "Stop"
$exe = Join-Path (Split-Path $PSScriptRoot -Parent) "publish\Gaska.SelfCheckout.Web.exe"
if (-not (Test-Path $exe)) { throw "Brak $exe – najpierw uruchom deploy\publish.ps1" }
if (Get-Service GaskaKasaSCO -ErrorAction SilentlyContinue) {
    Stop-Service GaskaKasaSCO -ErrorAction SilentlyContinue
    sc.exe config GaskaKasaSCO binPath= "`"$exe`"" | Out-Host
} else {
    sc.exe create GaskaKasaSCO binPath= "`"$exe`"" start= delayed-auto DisplayName= "GĄSKA Kasa samoobsługowa" | Out-Host
}
if ($User) { sc.exe config GaskaKasaSCO obj= $User password= $Password | Out-Host }
sc.exe failure GaskaKasaSCO reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Host
sc.exe start GaskaKasaSCO | Out-Host
