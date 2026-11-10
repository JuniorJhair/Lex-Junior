param(
    [Parameter(Mandatory = $false)]
    [string]$Version = "1.0.0",

    [Parameter(Mandatory = $false)]
    [string]$Notes = "Lanzamiento oficial de Lex Junior Browser v1.0.0 con escudo anti-anuncios nativo, 0 telemetria, amplificador de volumen 300% y detector multimedia bajo demanda.",

    [switch]$PublishToGitHub,

    [switch]$SkipLocalInstall
)

$ErrorActionPreference = "Stop"
$csprojPath = Join-Path $PSScriptRoot "LexBrowser\LexBrowser.csproj"
$distDir = Join-Path $PSScriptRoot "dist"
$publishTempDir = Join-Path $distDir "publish_tmp"
$setupExePath = Join-Path $distDir "LexJuniorSetup.exe"

Write-Host "1. Actualizando version a $Version en LexBrowser.csproj..." -ForegroundColor Cyan
$rawCsproj = [System.IO.File]::ReadAllText($csprojPath, [System.Text.Encoding]::UTF8)
$csproj = $rawCsproj -replace "<Version>.*?</Version>", "<Version>$Version</Version>"
$csproj = $csproj -replace "<AssemblyVersion>.*?</AssemblyVersion>", "<AssemblyVersion>$Version.0</AssemblyVersion>"
$csproj = $csproj -replace "<FileVersion>.*?</FileVersion>", "<FileVersion>$Version.0</FileVersion>"
if ($csproj -ne $rawCsproj) {
    [System.IO.File]::WriteAllText($csprojPath, ($csproj.TrimEnd() + [Environment]::NewLine), (New-Object System.Text.UTF8Encoding($false)))
}

$webIndexPath = Join-Path $PSScriptRoot "website\index.html"
if (Test-Path $webIndexPath) {
    $webHtml = [System.IO.File]::ReadAllText($webIndexPath, [System.Text.Encoding]::UTF8)
    $updatedHtml = $webHtml -replace '<span id="hero-release-text">Lanzamiento Oficial v[0-9]+\.[0-9]+\.[0-9]+', "<span id=`"hero-release-text`">Lanzamiento Oficial v$Version"
    if ($updatedHtml -ne $webHtml) {
        [System.IO.File]::WriteAllText($webIndexPath, $updatedHtml, (New-Object System.Text.UTF8Encoding($false)))
    }
}

if (Test-Path $publishTempDir) { Remove-Item $publishTempDir -Recurse -Force }
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

Write-Host "2. Generando instalador autonomo comprimido (LexJuniorSetup.exe)..." -ForegroundColor Cyan
dotnet publish $csprojPath -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -p:NuGetAudit=false `
    -o $publishTempDir

$builtExe = Join-Path $publishTempDir "LexBrowser.exe"
if (!(Test-Path $builtExe)) {
    throw "No se genero el ejecutable esperado en $builtExe"
}

Copy-Item $builtExe $setupExePath -Force
Remove-Item $publishTempDir -Recurse -Force

$sizeMb = [Math]::Round((Get-Item $setupExePath).Length / 1MB, 2)
Write-Host "[OK] Instalador generado exitosamente: $setupExePath ($sizeMb MB)" -ForegroundColor Green

# 2.1 Actualizar accesos directos locales y notificar a Windows Shell
if (-not $SkipLocalInstall) {
try {
    Write-Host "Actualizando accesos directos locales con nuevo logo oficial..." -ForegroundColor Cyan
    $wsh = New-Object -ComObject WScript.Shell
    $localApp = [Environment]::GetFolderPath('LocalApplicationData')
    $iconFile = Join-Path $localApp 'Programs\LexJuniorBrowser\app.ico'
    $exeFile = Join-Path $localApp 'Programs\LexJuniorBrowser\LexBrowser.exe'
    $installFolder = Join-Path $localApp 'Programs\LexJuniorBrowser'

    $srcIcon = Join-Path $PSScriptRoot 'LexBrowser\Assets\app.ico'
    if (Test-Path $srcIcon) {
        if (!(Test-Path $installFolder)) { New-Item -ItemType Directory -Path $installFolder -Force | Out-Null }
        Copy-Item $srcIcon $iconFile -Force
    }

    try {
        Copy-Item $setupExePath $exeFile -Force -ErrorAction SilentlyContinue
        $copiedOk = $false
        if (Test-Path -LiteralPath $exeFile) {
            $srcHash = (Get-FileHash -LiteralPath $setupExePath -Algorithm SHA256).Hash
            $dstHash = (Get-FileHash -LiteralPath $exeFile -Algorithm SHA256).Hash
            $copiedOk = ($srcHash -eq $dstHash)
        }
        if ($copiedOk) {
            Write-Host "[OK] Binario instalado localmente actualizado en $exeFile" -ForegroundColor Green
        } else {
            Write-Host "[AVISO] No se actualizo el binario local (esta en uso). Cierra Lex Junior y vuelve a ejecutar, o usa Actualizar desde la app." -ForegroundColor Yellow
        }
    } catch {
        Write-Host "[AVISO] No se actualizo el binario local (esta en uso). Cierra Lex Junior y vuelve a ejecutar, o usa Actualizar desde la app." -ForegroundColor Yellow
    }

    $deskPath = [Environment]::GetFolderPath('Desktop')
    $deskFile = Join-Path $deskPath 'Lex Junior Browser.lnk'
    if (Test-Path $deskFile) {
        Remove-Item $deskFile -Force -ErrorAction SilentlyContinue
        $link = $wsh.CreateShortcut($deskFile)
        $link.TargetPath = $exeFile
        $link.WorkingDirectory = $installFolder
        $link.IconLocation = "$iconFile,0"
        $link.Save()
        (Get-Item $deskFile).LastWriteTime = (Get-Date)
    }

    $taskbarPath = Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
    if (Test-Path $taskbarPath) {
        Get-ChildItem -Path $taskbarPath -Filter '*Lex*.lnk' | ForEach-Object {
            $p = $_.FullName
            Remove-Item $p -Force -ErrorAction SilentlyContinue
            $link = $wsh.CreateShortcut($p)
            $link.TargetPath = $exeFile
            $link.WorkingDirectory = $installFolder
            $link.IconLocation = "$iconFile,0"
            $link.Save()
            (Get-Item $p).LastWriteTime = (Get-Date)
        }
    }
} catch {
    Write-Host "Nota: No se pudieron actualizar accesos locales: $($_.Exception.Message)" -ForegroundColor Yellow
}
}
else {
    Write-Host "2.1 [SKIP] Manteniendo instalacion local sin sobrescribir (-SkipLocalInstall)." -ForegroundColor Yellow
}

if ($PublishToGitHub) {
    $ghExe = "C:\Program Files\GitHub CLI\gh.exe"
    if (!(Test-Path $ghExe)) { $ghExe = "gh" }

    $tag = "v$Version"
    Write-Host "3. Publicando Release $tag en GitHub (JuniorJhair/Lex-Junior)..." -ForegroundColor Cyan
    $ErrorActionPreference = "Continue"
    & $ghExe release view $tag --repo JuniorJhair/Lex-Junior 2>$null | Out-Null
    $exists = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = "Stop"

    if ($exists) {
        & $ghExe release upload $tag $setupExePath --clobber --repo JuniorJhair/Lex-Junior
    }
    else {
        & $ghExe release create $tag $setupExePath --title "Lex Junior Browser $tag" --notes $Notes --repo JuniorJhair/Lex-Junior
    }
    Write-Host "[OK] Release $tag publicado en GitHub!" -ForegroundColor Green
    Write-Host "Enlace directo permanente para tu web en Cloudflare:" -ForegroundColor Yellow
    Write-Host "https://github.com/JuniorJhair/Lex-Junior/releases/latest/download/LexJuniorSetup.exe" -ForegroundColor White
}
