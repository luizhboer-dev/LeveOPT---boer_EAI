# Gera o instalador do Leve: testa, publica os dois executáveis, compila o Setup (Inno Setup 6) e calcula o SHA-256.
# Uso (na pasta leve-next):
#   powershell -ExecutionPolicy Bypass -File packaging\build-installer.ps1 -Version 1.1.0
param([string]$Version = "1.1.0")

$ErrorActionPreference = "Stop"
$packaging = $PSScriptRoot
$root = Split-Path -Parent $packaging
$out = Join-Path $packaging "out"
$app = Join-Path $out "app"

function Step($text) { Write-Host "`n== $text" -ForegroundColor Green }
function Check($what) { if ($LASTEXITCODE -ne 0) { throw "$what falhou (código $LASTEXITCODE)" } }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Step "1/4 testes"
dotnet test (Join-Path $root "tests\Leve.Core.Tests\Leve.Core.Tests.csproj") -c Release
Check "dotnet test"

Step "2/4 publicando Leve.exe e Leve.Agent.exe"
dotnet publish (Join-Path $root "src\Leve.App\Leve.App.csproj") -c Release -o $app "-p:Version=$Version"
Check "publish do Leve"
dotnet publish (Join-Path $root "src\Leve.Agent\Leve.Agent.csproj") -c Release -o $app "-p:Version=$Version"
Check "publish do agente"

Step "3/4 gerando o instalador"
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 não encontrado. Instale com:  winget install JRSoftware.InnoSetup" }

& $iscc "/DAppVersion=$Version" (Join-Path $packaging "Leve.iss")
Check "Inno Setup"

Step "4/4 SHA-256"
$setup = Join-Path $out "Leve-Setup-$Version.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash
"$hash  Leve-Setup-$Version.exe" | Set-Content (Join-Path $out "Leve-Setup-$Version.sha256.txt") -Encoding ascii

Write-Host "`nPronto: $setup" -ForegroundColor Green
Write-Host "SHA-256: $hash"
Write-Host "Mande o instalador por um canal e o SHA-256 por outro (ex.: Drive + WhatsApp)."
