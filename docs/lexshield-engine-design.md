# Lex Junior — Diseño del Motor AdBlock Declarativo (Histórico Pre-Unificación)

> **Nota histórica:** Propuesta inicial de diseño previa a la unificación en `LexShield Core` (`feature/escudo-unificado`). Para la arquitectura vigente y mediciones finales, consulta [`LEXSHIELD_PARIDAD.md`](LEXSHIELD_PARIDAD.md), [`UNIFICACION_ESCUDO.md`](UNIFICACION_ESCUDO.md) y [`REPORTE_UNIFICACION_FINAL.md`](REPORTE_UNIFICACION_FINAL.md).

**Fecha:** 2026-11-15  
**Rama / Base:** `master` @ `1586ec4` (tag de respaldo `backup-pre-adblock-brave`, snapshot físico en `_backups/pre-adblock-brave-20261115-0424`)  
**Objetivo:** Reemplazar el bloqueador GENERAL de Lex Junior (`AdBlockFilter`, `AdBlockResources`, `WebShield`, `ShieldExemptions`) por un motor declarativo en C#/.NET 8 inspirado en la semántica verificada de `adblock-rust-master/` (`v0.13.5`, MPL-2.0), manteniendo **estrictamente aislados YouTube y Dailymotion** y cumpliendo un presupuesto estricto de memoria (**objetivo ≤ +50 MB, tope máximo +100 MB RAM** sobre la línea base de 150–300 MB).

---

## 1. Línea Base y Hallazgos Verificados (Antes del Cambio)

### 1.1 Restricción de Toolchain y Motor de Referencia
- En este equipo se verificó `.NET SDK 10.0.301` y `Node.js v22.15.0`. **`cargo`/`rustc` no están instalados**.
- Además, `adblock-rust-master/` expone librería Rust (`src/lib.rs`) y addon Node (`js/`), **no** `cdylib` con ABI C. Por ello, se reimplementa la semántica en C#/.NET 8 reutilizando el dataset real y recursos de `adblock-rust-master/data/brave/`.
- `adblock-rust-master/data/update-lists.js` requiere la variable de entorno `BRAVE_SERVICE_KEY`; por tanto, el dataset compilado parte de una **instantánea congelada** de:
  - `adblock-rust-master/data/brave/brave-main-list.txt` (171,227 reglas activas)
  - `adblock-rust-master/data/brave/brave-unbreak.txt` (156 reglas activas)
  - `adblock-rust-master/data/brave/coin-miners.txt` (283 reglas activas)
  - `adblock-rust-master/data/brave/brave-resources.json` (211 recursos: JS, XML/VAST/VMAP, imágenes PNG/GIF, audio/video/HTML/JSON noop)
- En Fase 2 se documentará en `THIRD_PARTY_NOTICES.txt` (MPL-2.0 para `adblock-rust`/`coin-miners` y atribuciones GPLv3/uAssets/EasyList de `brave-resources.json` y listas compiladas) junto con el script reproducible de recompilación (`Tools/compile_brave_shield.py`).

### 1.2 Benchmark Línea Base (Motor Legacy)
Medido con `scratch/bench` (`Debug` con `-p:Optimize=true`, 2,000 URLs mixtas × 500 rondas = 1,000,000 llamadas):
- `AdBlockFilter.ShouldBlock`: **3,018 ms / 1,000,000 = 3,019 ns/llamada** (~3.0 µs/call), `blocked/round=400`.
- `AdBlockResources.TryGetRedirectStub`: **5,985 ms / 1,000,000 = 5,986 ns/llamada**, `stubs/round=209`.
- `Managed heap delta` (init+warmup): **+498 KB**.
- `LexBrowser.Tests` línea base: **608/608 passed, 0 failed, 0 skipped** (9 s).

---

## 2. Resultados de los Spikes de Fase 1

### 2.1 Spike B — Formato Binario Precompilado, Memoria RAM, Arranque y Latencia
Se construyó un compilador offline (`scratch/spike_b/compile_brave_spike.py`) y un cargador C# `.NET 8` (`scratch/spike_b/SpikeB.csproj` en `-c Release`) empaquetando las 3 listas de Brave + `brave-resources.json` en formato binario compacto (`LEXSHLD1`):

- **Auditoría exacta de reglas compiladas** (`171,666` líneas activas entre los 3 archivos):
  - Red — tabla rápida `PureHost` (`||host^` con opciones básicas sin conflictos): **99,933** hosts únicos (`2,035` con múltiples reglas/colisiones movidos a `PatternRules`).
  - Red — `PatternRules` (patrones con `/`, `*`, `^`, `$domain=`, `$from=`, `$to=`, `$denyallow=`, `$important`, `$redirect`, `$redirect-rule`, `$generichide`, `$popup`, excepciones `@@`): **27,002** reglas, con bucket wildcard fallback (`bucket[0]`) de solo **167** reglas.
  - Red — `$badfilter` resuelto en tiempo de compilación (`28` coincidencias canónicas canceladas; se ampliará normalización de opciones en Fase 2).
  - Cosmética — **8,897** clases genéricas simples (`.cls`), **4,263** IDs genéricos simples (`#id`), **84** selectores complejos por clase, **70** por ID, **532** genéricos misceláneos, y **33,940** claves host/entidad (`entity.*`) específicas.
  - Descartadas explícitamente como **No-Objetivos**:
    - YouTube/Dailymotion puros: `67` red + `62` cosmética (además aislados por router en runtime).
    - Full `/regex/` (493) y modificadores fuera de alcance (`$csp` 95, `$removeparam` 264, `$cname` 29, `$urlskip` 234, `$header` 49, `$ipaddress` 47, `$strict1p/3p` 36, `$tag` 65 -> total ~799 red).
    - Cosmética no soportada por `adblock-rust` (`##^` 157, `#%#` 1 -> 158 total).
    - Scriptlets `trusted-*`: marcados con bit de permiso privilegiado (desactivados por defecto según `BravePermissionPolicy`).

- **Medición real en `.NET 8 Release` (`SpikeB.csproj`)**:
  - Tamaño comprimido del binario único (`brave-shield-spike.bin`): **2.82 MB** (10.10 MB sin comprimir).
  - Tiempo de carga en frío (`SpikeEngine.Load` desde disco, ejecutable en hilo de fondo sin bloquear UI): **70.9 ms**.
  - **Delta Managed Heap**: **+12.07 MB** tras carga (**+12.80 MB** tras warm-up).
  - **Delta Private Memory (`PrivateMemorySize64`)**: **+15.37 MB** -> **~3.2x por debajo del objetivo (+50 MB) y ~6.5x por debajo del tope (+100 MB)**.
  - **Velocidad `ShouldBlock` (1,000,000 llamadas)**: **291 ms = 292 ns/llamada** en búsqueda por host+token (~10x más rápido en ruta pura que Legacy `3,019 ns/call`; la versión completa de Fase 2 con tokenización multi-token y comprobación de `check_parameterised` completo se medirá igualmente frente al baseline).
- **Mejora para Fase 2**: Almacenar los blobs de reglas cosméticas mediante **deduplicación por pool de payloads** (evita repetir listas de ~300 dominios como `animeyt.es` L36123 o `hentaila.com` L36221) y usar arreglos planos (`ulong[]`, `PatRec[]`) o diccionarios pre-dimensionados para reducir aún más las pausas GC.

### 2.2 Spike A — Ciclo de Vida WebView2 para Cosmética + Scriptlets por Host (Antes de Inline Scripts)
Inspección por reflexión de `Microsoft.Web.WebView2.Core.dll` (`1.0.4314.35`) verificó un detalle crítico:
- `CoreWebView2NavigationStartingEventArgs` **NO posee** `GetDeferral()`. Llamar `AddScriptToExecuteOnDocumentCreatedAsync` sin deferral desde `NavigationStarting` produce condición de carrera antes del primer `<script>` inline.
- `CoreWebView2WebResourceRequestedEventArgs` **SÍ posee** `CoreWebView2Deferral GetDeferral()`.
- **Estrategia diseñada para Fase 4**:
  1. **Bootstrap persistente mínimo (`ShieldBootstrapScriptId`)**: 1 script compacto registrado en `RefreshTabShieldScriptsAsync`, con observador por demanda (`MutationObserver` + debounce/idle) de clases `.class` / `#id` visibles -> envía lote único por nombres nuevos vía `window.chrome.webview.postMessage({ type: "lex_shield_cosmetic_query", ... })`, recibe CSS genérico filtrado (si `!generichide && policy.Cosmetic && !IsYtDm`) y lo inyecta en `<style id="lex-shield-gen">`. Evita volcar 13,000 selectores genéricos al DOM.
  2. **Scriptlets (`##+js(...)`) y CSS específico de host a `document_start`**:
     - En `WebResourceRequested` cuando `e.ResourceContext == Document`: si el host destino (principal o iframe no-YT/DM) posee scriptlets/cosmética específica y aún no está activo en el cache LRU de la pestaña (`tab.BraveActiveHosts`), tomar `var deferral = e.GetDeferral();`, registrar por `core.AddScriptToExecuteOnDocumentCreatedAsync(...)` un script **con guardia estricta de hostname** (`if (location.hostname === ... || ...)` + dedup `window.__lexBraveApplied`), y liberar `deferral.Complete()` en `finally`.
     - Las páginas sin scriptlets (navegación general como Google, Wikipedia, GitHub) tienen **0 deferrals y 0 ms de bloqueo IPC**.
     - Al navegar el frame principal a otro dominio registrable, se limpian scripts previos de subframes en `NavigationStarting` / `RefreshTabShieldScriptsAsync`.
     - *Nota*: El comportamiento asíncrono real del runtime WebView2 en reproducción en vivo queda pendiente de verificación visual manual tras Fase 4/5; toda la composición de scriptlets (`resource_storage.rs`), escapes (`stringify_arg`), argumentos (`parse_scriptlet_args`) y sintaxis/ejecución JS en `node --check` y `node vm` fue verificada ya en Spike A (`animeyt.es` `rmnt`, `hentaila.com` `acs`, `set`, `nowoif` -> `rc=0` sin errores).

### 2.3 Spike C — Clasificación de Contextos WebView2 (`Stylesheet`, `Font`, `Media`, `Subdocument`)
Inspección directa del enum `CoreWebView2WebResourceContext`:
```
0: All, 1: Document, 2: Stylesheet, 3: Image, 4: Media, 5: Font, 6: Script,
7: XmlHttpRequest, 8: Fetch, 9: TextTrack, 10: EventSource, 11: Websocket,
12: Manifest, 13: SignedExchange, 14: Ping, 15: CspViolationReport, 16: Other
```
- **Diferencia clave respecto a Chromium `webRequest` / `adblock-rust`**:
  1. `CoreWebView2WebResourceContext.Document` engloba **tanto `document` (main_frame) como `subdocument` (iframe)**! En `WebResourceRequested`, leemos `e.Request.Headers.GetHeader("Sec-Fetch-Dest")` dentro de `try/catch`: si vale `"iframe"` o `"frame"` (o si proviene de contexto no-mainframe con header presente) se clasifica como **`FROM_SUBDOCUMENT`**; de lo contrario **`FROM_DOCUMENT`**.
  2. `CoreWebView2WebResourceContext.Fetch` debe mapearse a **`FROM_XMLHTTPREQUEST`** (además de `FROM_OTHER` si aplica? Mejor: en listas ABP/uBO `$xhr` cubre `fetch()` y `XMLHttpRequest`; mapear `Fetch` y `XmlHttpRequest` a `FROM_XMLHTTPREQUEST`, evitando falsos negativos en reglas `$xhr` y `$~xhr`).
  3. **Registro de nuevos filtros en `MainWindow.Tabs.cs`**:
     - Actualmente solo se registran `Document, Script, XmlHttpRequest, Fetch, Image, Ping, Other`.
     - `brave-main-list.txt` contiene **125** reglas `$stylesheet/$css`, **10** `$font`, **11** `$websocket`, **198** `$media`, más **>100,000** reglas `||adhost^` (`FROM_ALL_TYPES`).
     - En Fase 2 registraremos adicionalmente `Stylesheet`, `Font` y `Websocket` (bajo volumen por carga de página, ~3-20 req/página × <1 µs en C#). Respecto a `Media`: evaluar en benchmark/paridad si registrar `CoreWebView2WebResourceContext.Media` compensa para anuncios pre-roll `<video src="...mp4">` frente a solicitudes `Range:` (se implementará con bandera configurable o evaluación directa en Fase 2/5).
  4. **`$popup` (3,099 reglas en `brave-main-list.txt`)**: `adblock-rust` las descarta, pero Lex Junior intercepta `core.NewWindowRequested` y navegación sospechosa. Compilaremos `$popup` bajo un bit dedicado **`FROM_POPUP`** evaluado únicamente en `ShouldBlockPopup(...)`, mejorando bloqueo de popups en sitios como anime/descargas sin afectar tráfico subresource normal.

---

## 3. Arquitectura Modular Objetivo (Fases 2–6)

```
MainWindow.Tabs.cs / MainWindow.WindowChrome.cs
  ├── SitePolicy.Resolve(...)            [Políticas Normal/Compat/Off + módulos + motor Legacy|Brave]
  └── ShieldRouter (única fachada pública)
        ├── [YouTube Isolated]     -> MediaShieldRules.cs + YouTubeOptimizer.cs (INTOCABLES)
        ├── [Dailymotion Isolated] -> DailymotionRules.cs + DailymotionOptimizer.cs (INTOCABLES)
        └── [General Engine]
              ├── Engine == "Legacy" -> LegacyShieldEngine (AdBlockFilter + AdBlockResources + WebShield)
              └── Engine == "Brave"  -> BraveShieldEngine
                                           ├── BraveNetworkEngine   (Tier 1 PureHost + Tier 2 TokenBuckets)
                                           ├── BraveResourceStore   (stubs $redirect / $redirect-rule + permisos)
                                           ├── BraveCosmeticEngine  (Host CSS + Procedural + OnDemand Generic)
                                           └── BraveScriptletEngine (brave-resources.json, PermissionMask == 0)
```

### Garantías de Aislamiento Inviolables
1. **YouTube**: `YouTubeOptimizer.cs`, `MediaShieldRules.cs`, `YouTubeOptimizerTests.cs` no se editan. Todo host `youtube.com`, `youtu.be`, `youtube-nocookie.com`, `googlevideo.com`, `ytimg.com` se excluye tanto en el compilador offline como en tiempo de ejecución en `BraveShieldEngine`.
2. **Dailymotion**: `DailymotionOptimizer.cs` no se edita. Las reglas de red y stub (`dmads.dailymotion.com`, exención de streams `dailymotion.com`/`dmcdn.net`, `nativeads.js` -> `window.__NATIVEADS_CANARY__ = true;`) se extraen a **`DailymotionRules.cs`** sin cambiar comportamiento; se preserva la señal `dm_real_video_detected` y en Fase 5 se alinea `RefreshTabShieldScriptsAsync` para consultar `SitePolicy.Resolve(..., tab.TemporaryCompatMode).DailymotionAdapter`.
3. **Sitios Sensibles**: `ShieldExemptions.IsPaymentOrAuthHost` y `ShieldExemptions.BigSites` (`miro.com`, `figma.com`, `canva.com`, `google.com`, etc.) y la lógica anti-tab-under siguen protegiendo navegación crítica en ambos motores.

### No-Objetivos Declarados (No Implementar / No Simular)
- `$csp`, `$removeparam`, `$cname`
- Filtrado HTML (`##^`), sintaxis AdGuard (`#%#`, `#$#`), ubicaciones regex en cosmética
- Expresiones regulares completas (`/regex/`) en red salvo que Fase 5 demuestre brecha crítica con medición
- Modificar `MediaExtractorService.cs`, `Core/Tv/**`, `Core/Pdf/**`
- Ejecutar `git push` o crear releases públicos

---

## 4. Plan de Fases Siguientes

- **Fase 2**: `DailymotionRules.cs` + `IShieldEngine` + `ShieldRouter` + `ShieldSettings.Engine = "Legacy" | "Brave"` + compilador reproducible `Tools/compile_brave_shield.py` + binario `Assets/shield/brave-shield.lex` + `BraveNetworkEngine` + carga asíncrona en segundo plano + `THIRD_PARTY_NOTICES.txt` + tests exhaustivos de aislamiento y semántica `check_parameterised`.
- **Fase 3**: `BraveResourceStore` integrado en `$redirect` / `$redirect-rule` (con prioridades, exenciones `BigSites`/YT/DM y toggle `ImaStub`/`EnableImaSdkStub`).
- **Fase 4**: Cosmética por host/entidad, `#@#`, `$generichide`, acciones procedurales, consulta bajo demanda de `.class`/`#id` genéricos e inyector de scriptlets `brave-resources.json` (`PermissionMask == 0`) con tests JS en `Node.js` + `WebResourceRequested(Document)` deferral.
- **Fase 5**: Alineación de `DailymotionAdapter` con `SitePolicy`, limpieza selectiva de heurísticas redundantes si se demuestra paridad, conservación de guardas mínimos anti-popunder/video-embed necesarios y comparación cuantitativa `Legacy` vs `Brave`.
- **Fase 6**: Suite completa `dotnet test`, build `Release`, benchmark final de RAM y latencia comparativo y reporte final honesto.
