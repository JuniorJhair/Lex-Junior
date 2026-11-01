# Lanzador rápido de Lex Junior Browser
$ErrorActionPreference = "SilentlyContinue"

$projectDir = Join-Path $PSScriptRoot "LexBrowser"
$binDir = Join-Path $projectDir "bin\Debug\net8.0-windows"
$exePath = Join-Path $binDir "LexBrowser.exe"

# Rotar binarios si estuvieran retenidos por un proceso previo
if (Test-Path $exePath) {
    $rand = [System.IO.Path]::GetRandomFileName()
    Move-Item -Path $exePath -Destination "$exePath.$rand.bak" -Force -ErrorAction SilentlyContinue
    $dllPath = Join-Path $binDir "LexBrowser.dll"
    if (Test-Path $dllPath) {
        Move-Item -Path $dllPath -Destination "$dllPath.$rand.bak" -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Compilando Lex Junior Browser..." -ForegroundColor Cyan
dotnet build (Join-Path $projectDir "LexBrowser.csproj") --verbosity minimal

if (Test-Path $exePath) {
    Write-Host "Iniciando Lex Junior Browser..." -ForegroundColor Green
    Start-Process $exePath
}
else {
    Write-Host "Error: No se encontró LexBrowser.exe" -ForegroundColor Red
}
    