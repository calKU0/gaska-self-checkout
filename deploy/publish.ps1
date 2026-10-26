# Publikacja aplikacji kasy – ten sam profil co w Visual Studio (Publish → "SelfCheckout"): Release, win-x86 (XL API),
# wynik w .\publish (folder nie jest czyszczony – data\ i logs\ zostają).
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
dotnet publish "$root\src\Gaska.SelfCheckout.Web\Gaska.SelfCheckout.Web.csproj" -p:PublishProfile=SelfCheckout
if ($LASTEXITCODE -ne 0) { throw "Publikacja nie powiodła się" }
if (-not (Test-Path "$root\publish\appsettings.Local.json")) { Write-Warning "Brak appsettings.Local.json – uzupełnij dane dostępowe na stanowisku" }
Write-Host "Opublikowano do $root\publish"
