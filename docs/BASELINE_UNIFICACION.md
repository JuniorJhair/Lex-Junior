# Línea Base Pre-Unificación (`BASELINE_UNIFICACION.md`)

**Fecha de captura:** 2026-11-15  
**Commit base (`master`):** `b021ab2` (`fix(shield): capture subframe subresources with SourceKinds.All and deduplicate ShieldLogger`)  
**Tag de respaldo:** `backup-pre-unificacion-20261115-2227`  
**Rama de trabajo:** `feature/escudo-unificado`

---

## 1. Entorno y Protocolo Reproducible

- **SO:** Microsoft Windows 10.0.26300 (Build 26300) · **Arquitectura:** X64 · **CPU lógicos:** 12
- **Runtime .NET:** `.NET 8.0.34` (compilación `Release`)
- **WebView2 Runtime:** `157.0.4341.46`
- **Aislamiento de pruebas:** `LEX_TEST_SETTINGS_PATH` y `UserDataFolder` temporales en `%TEMP%` (`%LOCALAPPDATA%\LexBrowser\settings.json` real intacto).
- **Compilación Release:** `0 Advertencia(s) C#`, `0 Errores` (`NU1900` offline en NuGet).
- **Suite `LexBrowser.Tests` (`Release`):** `659/659` pasadas (`0` fallidas, `0` omitidas, `8 s`).

---

## 2. Benchmarks Deterministas en Memoria (5 rondas post-warmup, `BaselineProbe audit`)

| Métrica | Medición Baseline (`Engine=Brave`) | Rondas (1..5) |
| :--- | :--- | :--- |
| Carga fría de red (`BraveShieldData.LoadFromPath`) | **38.24 ms** | — |
| Descompresión perezosa `ResourceStore` (`223` recursos) | **11.13 ms** | — |
| Descompresión perezosa `CosmeticEngine` (`42,518` hosts) | **155.51 ms** | — |
| Delta memoria tras carga red (`Heap` / `Private` / `WS`) | **+5.38 MB** / **+10.26 MB** / **+9.31 MB** | — |
| Delta memoria con todo caliente (`Heap` / `Private` / `WS`) | **+21.66 MB** / **+39.82 MB** / **+43.01 MB** | — |
| `ShieldRouter.EvaluateWebResource` (`100k` req/ronda) | Media **14,094.4 ns/req** (máx **16,489.9 ns**) | `[16489.9, 11306.7, 11302.7, 16312.9, 15059.7]` |
| `ShieldRouter.BuildDocumentPlanJson` (`1k` docs/ronda) | Media **0.3020 ms/doc** | `[0.5389, 0.2905, 0.2118, 0.2409, 0.2282]` |
| `CosmeticEngine.QueryGenericCss` (`10k` queries/ronda) | Media **17,713.7 ns/query** | `[25063.5, 13266.0, 15238.5, 21612.9, 13387.5]` |

---

## 3. Corpus en Vivo Pre-Unificación (`BaselineProbe live`, WebView2 `%TEMP%`, `DiagnosticMode=True`)

| Sitio | URL probada | Navegación | Conteos `ShieldLogger` (`403` / `204` / `Stub200` / `Subframe` / `Allowed`) | Observación real verificada |
| :--- | :--- | :--- | :--- | :--- |
| **HentaiLA** | `https://hentaila.com/media/hitozukiai-ga-nigate-na-miboujin-no-yukionna-san-to-noroi-no-yubiwa/6` | `NavOk=True(err=0)`, `4` frames | `403=9`, `204=0`, `Stub200=1`, `SubBlocked=3`, `Allowed=13` (`AllowedAdtngEtahub=0`) | Bloqueados `ssl.p.jwpcdn.com/player/v/8.30.0/vast.js` (`403`), 3 iframes `a.adtng.com`, `tsyndicate`. Dentro de `cdn.dotanborito.net`: `videos=[{ready:4, dur:428}]`, `jwAdClass=false`. Hallazgo menor: XHR `.m3u8`/segs de `cdn.dotanborito.net` se registraron con `Origin: hentaila.com` / `Subframe=False` por `Referrer-Policy: no-referrer`. |
| **AnimeYT** | `https://animeyt.cc/` | `NavOk=True(err=0)`, `4` frames | `403=8`, `204=0`, `Stub200=1` (`gtag/js`), `Allowed=37` | Cargado limpio (`AnimeYT - Ver Anime...`). Bloqueó `visariomedia.com`, `d13k7prax1yi04.cloudfront.net`, `c.adsco.re`, `ssfustmve.com`. Respetó Turnstile (`challenges.cloudflare.com`). |
| **Dailymotion** | `https://www.dailymotion.com/video/x8t73c2` | `NavOk=True(err=0)`, `2` frames | `403=1` (`dmads.min.js`), `Stub200=1`, `Allowed=62` | **Bug detectado en baseline:** `DailymotionRules.TryGetRedirectStub` (`L121`) interceptó `https://dmxleo.dailymotion.com/cdn/manifest/video/x8t73c2.m3u8?...` devolviendo stub VMAP vacío (`200`), dejando el video en `ready:0, dur:-1`. Requiere arreglo en Corrida B. |
| **YouTube** | `https://www.youtube.com/watch?v=dQw4w9WgXcQ` | `NavOk=True(err=0)` tras redirect `themeRefresh=1`, `1` frame | `403=0`, `204=7`, `Stub200=2` (`ad_status.js`), `Allowed=45` | `videos=1`, `ytAd=false`, video reproduciendo (`0:02 / 3:33`) con `doubleclick` y `google.com/pagead/*` silenciados en `204`. |
| **Google** | `https://www.google.com/search?q=fotosintesis` | Redirigido a `/sorry/index` (`reCAPTCHA`) | `Allowed=27` | Entorno headless limpio recibió anti-bot `/sorry/index`; no se evade (queda para verificación manual). Auditoría unitaria determinista confirma `gen_204?` y `google.com/log` bloqueados (`Brave`) vs `complete/search`, `lens.google.com/v3/upload` y `async/fol` permitidos. |

---

## 4. Búsquedas Reproducibles Baseline (PowerShell)

**Comando ejecutado** (excluyendo `.git`, `.vs`, `bin`, `obj`, `_backups`, `_release`, `dist`, `UserData`, `COMO BLOQUEA BRAVE`, `Magma.apk_Decompiler.com` y binarios `.lex/.png/.jpg/.ico/.exe/.dll/.pdf/.pdb/.zip`):

```powershell
$ex = @('COMO BLOQUEA BRAVE','Magma.apk_Decompiler.com','bin','obj','_backups','_release','dist','.git','.vs','UserData')
$files = Get-ChildItem -Recurse -File | Where-Object { $p = $_.FullName; -not ($ex | Where-Object { $p -like "*\$_\*" }) -and $_.Extension -notin @('.lex','.png','.jpg','.ico','.exe','.dll','.pdf','.pdb','.zip') }
$braveHits = $files | Select-String -Pattern "brave" -CaseSensitive:$false
$ligeroHits = $files | Select-String -Pattern "\b(ligero|ligera|ligeros|ligeras|ultraligero|ultraligera|ultra-ligero|ultra-ligera|ultra ligero|liviano|livianos|liviana|livianas|peso pluma|lightweight|light-weight|ultra-light|ultralight)\b" -CaseSensitive:$false
```

- **Resultado `"brave"`:** **`848` líneas en `56` archivos** (incluye 8 coincidencias en `tools/__pycache__/compile_brave_shield.cpython-314.pyc` + código interno `Brave/*`, tests, UI Ajustes/popup/logs, docs, `THIRD_PARTY_NOTICES.txt`, **Brave Search** y **Brave Importer**).
- **Resultado `"ligero|..."`:** **`29` líneas en `19` archivos** (`NewTabPage.cs`, `website/index.html`, `README.md`, `InstallerWindow.xaml`, `SettingsWindow.xaml`, `MainWindow.xaml`, `MainWindow.Downloads.cs`, `MainWindow.Tabs.cs`, `Core/*.cs`, `docs/*.md`, `Assets/pdf/viewer.html`).
