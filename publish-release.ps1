param(
    [Parameter(Mandatory = $false)]
    [string]$Version = "1.0.0",

    [Parameter(Mandatory = $false)]
    [string]$Notes = "Lanzamiento oficial de Lex Junior Browser v$Version con escudo anti-anuncios nativo, 0 telemetría, amplificador de volumen 300% y detector multimedia bajo demanda.",

    [switch]$PublishToGitHub = $true
)

$ErrorActionPreference = "Stop"
$csprojPath = Join-Path $PSScriptRoot "LexBrowser\LexBrowser.csproj"
$distDir = Join-Path $PSScriptRoot "dist"
$publishTempDir = Join-Path $distDir "publish_tmp"
$setupExePath = Join-Path $distDir "LexJuniorSetup.exe"

Write-Host "1. Actualizando versión a $Version en LexBrowser.csproj..." -ForegroundColor Cyan
$csproj = Get-Content -Raw $csprojPath
$csproj = $csproj -replace "<Version>.*?</Version>", "<Version>$Version</Version>"
$csproj = $csproj -replace "<AssemblyVersion>.*?</AssemblyVersion>", "<AssemblyVersion>$Version.0</AssemblyVersion>"
$csproj = $csproj -replace "<FileVersion>.*?</FileVersion>", "<FileVersion>$Version.0</FileVersion>"
Set-Content -Path $csprojPath -Value $csproj -Encoding UTF8

if (Test-Path $publishTempDir) { Remove-Item $publishTempDir -Recurse -Force }
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

Write-Host "2. Generando instalador autónomo comprimido (LexJuniorSetup.exe)..." -ForegroundColor Cyan
dotnet publish $csprojPath -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $publishTempDir

$builtExe = Join-Path $publishTempDir "LexBrowser.exe"
if (!(Test-Path $builtExe)) {
    throw "No se generó el ejecutable esperado en $builtExe"
}

Copy-Item $builtExe $setupExePath -Force
Remove-Item $publishTempDir -Recurse -Force

$sizeMb = [Math]::Round((Get-Item $setupExePath).Length / 1MB, 2)
Write-Host "✔ Instalador generado exitosamente: $setupExePath ($sizeMb MB)" -ForegroundColor Green

if ($PublishToGitHub) {
    $ghExe = "C:\Program Files\GitHub CLI\gh.exe"
    if (!(Test-Path $ghExe)) { $ghExe = "gh" }

    $tag = "v$Version"
    Write-Host "3. Publicando Release $tag en GitHub (JuniorJhair/Lex-Junior)..." -ForegroundColor Cyan
    & $ghExe release view $tag --repo JuniorJhair/Lex-Junior 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        & $ghExe release upload $tag $setupExePath --clobber --repo JuniorJhair/Lex-Junior
    }
    else {
        & $ghExe release create $tag $setupExePath --title "Lex Junior Browser $tag" --notes $Notes --repo JuniorJhair/Lex-Junior
    }
    Write-Host "✔ Release $tag publicado en GitHub!" -ForegroundColor Green
    Write-Host "Enlace directo permanente para tu web en Cloudflare:" -ForegroundColor Yellow
    Write-Host "https://github.com/JuniorJhair/Lex-Junior/releases/latest/download/LexJuniorSetup.exe" -ForegroundColor White
}
