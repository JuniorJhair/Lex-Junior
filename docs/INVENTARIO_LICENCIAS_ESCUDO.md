# Inventario de Licencias y Terceros — Escudo LexShield y Activos Incluidos

**Fecha de auditoría:** 2026-11-15  
**Base Git:** `master` @ `b021ab2` / tag `backup-pre-unificacion-20261115-2227`  
**Propósito:** Identificar licencias reales y condiciones de redistribución **antes** de recompilar o empaquetar reglas/scriptlets adicionales en `LexBrowser.csproj` (`<EmbeddedResource>`) y `publish-release.ps1` (`PublishSingleFile=true`).  
**Alcance:** Inventario técnico informativo. No sustituye revisión legal ni decide políticas de licenciamiento.

---

## 1. Inventario de Listas, Recursos, Scriptlets y Motores del Escudo

| Activo / Lista | Ruta en repo | Origen upstream | Licencia detectada | Condición de redistribución | Riesgo en instalador binario (`PublishSingleFile`) | Decisión pendiente |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| Bundle compilado `.lex` | `LexBrowser/Assets/shield/brave-shield.lex` (3.08 MB, `LEXSHLD2`) | Compilado desde las 17 listas y 2 JSON inferiores | **Mixta: `GPL-3.0` + `GPL-3.0 OR CC BY-SA 3.0` + `MPL-2.0`** | Heredada de los insumos embebidos en `<EmbeddedResource>` | **ALTO** (reside dentro de `LexBrowser.dll` / `LexJuniorSetup.exe`) | Confirmar modelo de distribución / exclusión GPLv3 o apertura de código |
| Compilador offline | `tools/compile_brave_shield.py` | Propio (`Lex Junior`) leyendo snapshots locales | Propia del repo | — | Ninguno (no se distribuye en el binario) | Renombrar a nombre neutro en Corrida B |
| `brave-main-list.txt` (6.91 MB) | `COMO BLOQUEA BRAVE/adblock-rust-master/adblock-rust-master/data/brave/brave-main-list.txt` | `uBlockOrigin/uAssets` (`! Title: uBlock filters`) | **`GPL-3.0`** | Copyleft fuerte; derivados/combinados exigen código fuente bajo `GPL-3.0` | **ALTO** si se embebe compilado en binario cerrado | Decidir si se sustituye, se mantiene externo/descargable o se cumple `GPL-3.0` |
| `uBlockOrigin/` (`filters.txt`, `badware.txt`, `resource-abuse.txt`, `unbreak.txt`) | `COMO BLOQUEA BRAVE/adblock-rust-master/adblock-rust-master/data/uBlockOrigin/*` | `uBlockOrigin/uAssets` | **`GPL-3.0`** | Copyleft fuerte (`uAssets/LICENSE`) | **ALTO** si se embebe compilado en binario cerrado | Igual que fila anterior |
| `easylist.txt`, `easyprivacy.txt`, `easylistspanish.txt` (5 variantes en compilador) | `COMO BLOQUEA BRAVE/adblock-rust-master/**/data/easylist.to/*` | `easylist.to` | **`GPL-3.0` OR `CC BY-SA 3.0 Unported`** | Bajo `CC BY-SA 3.0`: atribución + derivados/índices compilados bajo `CC BY-SA 3.0` | **MEDIO/ALTO** según interpretación de obra derivada del índice binario | Confirmar atribución visible + separación o liberación del índice |
| `brave-resources.json` (216 entradas) | `COMO BLOQUEA BRAVE/adblock-rust-master/adblock-rust-master/data/brave/brave-resources.json` | `brave/adblock-rust` + recursos derivados de `uAssets` | **`MPL-2.0` + `24` scriptlets con cabecera `GPL-3.0`/FSF** (`amazon_ads.js`, `ampproject_v0.js`, etc.) | Los 24 scriptlets GPL/FSF imponen `GPL-3.0` si se incrustan en el paquete | **ALTO** mientras el JSON/blob incluya esos 24 scriptlets GPL embebidos | Depurar scriptlets `GPL-3.0` vs reemplazarlos por `MPL-2.0`/propios o aceptar GPL |
| `resources.json` (24 entradas) | `COMO BLOQUEA BRAVE/adblock-resources-master/adblock-resources-master/dist/resources.json` | `brave/adblock-resources` | **`MPL-2.0`** (con `7` scriptlets de consentimiento con mención `uAssets/gorhill`) | `MPL-2.0` exige publicar modificaciones al código licenciado MPL; revisar los 7 de uAssets | **BAJO-MEDIO** (`MPL-2.0` es compatible con binario separado/archivo modificado; verificar los 7 de uAssets) | Revisar los 7 archivos `*consent*.js` |
| Listas Brave (`adblock-lists-master` + `coin-miners.txt`) | `COMO BLOQUEA BRAVE/adblock-lists-master/*`, `coin-miners.txt` | `brave/adblock-lists` (`brave-unbreak`, `firstparty*`, `specific`, `sugarcoat`, `NoCoin`) | **`MPL-2.0`** (`adblock-lists-master/LICENSE`) | `MPL-2.0` (copyleft a nivel de archivo modificado + aviso) | **BAJO** (cumpliendo aviso `MPL-2.0` y enlace a fuentes/modificaciones) | Conservar atribución en `THIRD_PARTY_NOTICES.txt` y UI |
| Motor C# declarativo (`BraveNetworkEngine`, `Cosmetic`, `Scriptlet`, `Mask`, `Data`, `ResourceStore`, `Bridge`) | `LexBrowser/Core/AdBlock/Brave/*.cs` | Implementación C# propia inspirada en diseño público de `adblock-rust` (`MPL-2.0`) | Propia del repo (diseño inspirado en `MPL-2.0`) | Sin copia de código Rust; conservar crédito/nota de diseño si aplica | **BAJO** | Renombrar a `LexShield Core` en Corrida B |
| Stub IMA (`AdBlockResources.GoogleImaScript`) | `LexBrowser/Core/AdBlock/AdBlockResources.cs` (`L181-L841`) | Mozilla Firefox WebCompat shim (`mozilla-central/shims/google-ima.js`, modificado según uBO) | **`MPL-2.0`** (cabecera explícita en `L213-L215`) | Archivo cubierto por `MPL-2.0` | **BAJO** (mantener cabecera `MPL-2.0`) | Declarar en `THIRD_PARTY_NOTICES.txt` y UI |
| Módulos propios del escudo | `AdBlockFilter.cs`, `AdBlockResources.cs` (stubs XML/JS/GIF), `WebShield.cs`, `MediaShieldRules.cs`, `YouTubeOptimizer.cs`, `DailymotionOptimizer.cs`, `DailymotionRules.cs`, `CookieBannerShield.cs`, `PublicSuffix.cs`, `ShieldExemptions.cs`, `ShieldIsolation.cs`, `ShieldRouter.cs`, `SitePolicy.cs`, `ShieldModuleRegistry.cs` | Propios de `Lex Junior` (comportamiento de reglas YT basado en reglas públicas de Brave/uBO) | Propia del repo | No copian código fuente ajeno; `MediaShieldRules` y `YouTubeOptimizer` documentan reglas de referencia | **BAJO** | Unificar bajo `LexShield Core` |

---

## 2. Otras Librerías de Terceros Embebidas en el Binario (`LexBrowser.csproj`)

| Activo | Ruta | Origen | Licencia | Riesgo binario | Acción |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `hls.light.min.js` (v1.7.4) | `LexBrowser/Assets/tv/vendor/hls.light.min.js` | npm `hls.js` | **`Apache-2.0`** (ya en `THIRD_PARTY_NOTICES.txt`) | Ninguno cumpliendo `NOTICE`/licencia | Incluir en «Licencias y créditos» UI |
| `mpegts.js` (v1.8.2) | `LexBrowser/Assets/tv/vendor/mpegts.js` | npm `mpegts.js` | **`Apache-2.0`** (ya en `THIRD_PARTY_NOTICES.txt`) | Ninguno cumpliendo `NOTICE`/licencia | Incluir en «Licencias y créditos» UI |
| `pdf.min.js`, `pdf.worker.min.js` | `LexBrowser/Assets/pdf/` | Mozilla Foundation (`pdf.js`, 2023) | **`Apache-2.0`** | Añadir crédito explícito si falta | Agregar a `THIRD_PARTY_NOTICES.txt` y UI |
| `pdf-lib.min.js` | `LexBrowser/Assets/pdf/pdf-lib.min.js` | PDF-Lib bundle minificado | Solo figura cabecera **`Apache-2.0`** (Microsoft); licencia raíz del paquete no embebida en el `.min.js` | Verificar aviso/licencia upstream en origen antes de listar en UI | Confirmar texto de licencia en Corrida C |
| Paquetes NuGet (`Microsoft.Web.WebView2`, `System.Speech`, `Microsoft.Data.Sqlite.Core`, `SQLitePCLRaw.bundle_winsqlite3`) | `LexBrowser/LexBrowser.csproj` | Microsoft / Eric Sink (según NuGet) | No auditada individualmente en caché offline | Consultar metadatos NuGet locales en Corrida C | Incluir solo lo verificado |

---

## 3. Hallazgos Clave Para Decisión Del Usuario (Antes de Distribuir)

1. **`LexBrowser.csproj` línea 30** embebe `brave-shield.lex` como `<EmbeddedResource>` y `publish-release.ps1` compila un único ejecutable (`-p:PublishSingleFile=true`). Actualmente el `.lex` contiene `brave-main-list.txt`, 4 archivos `uBlockOrigin/*`, 3 listas `easylist.to` y **24 recursos con cabecera `GPL-3.0`** en `brave-resources.json`.
2. **`AdBlockResources.GoogleImaScript`** ya lleva licencia **`MPL-2.0`** (más permisiva respecto a mezcla con código propietario en binario que `GPL-3.0`), mientras que `brave-resources.json` mezcla recursos `MPL-2.0` con derivados `GPL-3.0`.
3. En **Corrida B**, las reglas propias de Lex (`AdBlockFilter` filtradas y stubs VAST/VMAP/IMA/popads) pueden integrarse como lista `"extra"` propia/`MPL-2.0` sin añadir riesgo copyleft nuevo; la decisión sobre qué hacer con las listas `GPL-3.0` (`uAssets`) en futuras releases públicas queda pendiente de confirmación tuya (p. ej.: conservar bundle externo actualizable / elegir listas no-GPL / publicar bajo licencia compatible).
