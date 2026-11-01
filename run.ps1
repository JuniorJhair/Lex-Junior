# Lanzador rápido de Lex Junior Browser
# Cierra cualquier instancia previa (evita archivos bloqueados y la acumulación de .bak), compila y abre.
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$projectDir = Join-Path $PSScriptRoot "LexBrowser"
$project = Join-Path $projectDir "LexBrowser.csproj"
$exePath = Join-Path $projectDir "bin\$Configuration\net8.0-windows\LexBrowser.exe"

Get-Process -Name "LexBrowser" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

Write-Host "Compilando Lex Junior Browser ($Configuration)..." -ForegroundColor Cyan
dotnet build $project -c $Configuration --verbosity minimal

if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: la compilación falló." -ForegroundColor Red
    exit $LASTEXITCODE
}

if (Test-Path $exePath) {
    Write-Host "Iniciando Lex Junior Browser..." -ForegroundColor Green
    Start-Process $exePath
}
else {
    Write-Host "Error: no se encontró $exePath" -ForegroundColor Red
    exit 1
}
