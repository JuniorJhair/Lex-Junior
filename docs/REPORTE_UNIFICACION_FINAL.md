# Reporte Final de Unificación — `LexShield Core` (`feature/escudo-unificado`)

**Fecha:** 2026-11-16  
**Tag de respaldo:** `backup-pre-unificacion-20261115-2227` (`master` @ `b021ab2`)  
**Alcance ejecutado:** Corridas **A**, **B** y **C** completas sin cambio de versión, publicación ni push remoto.

---

## 1. Resumen de Arquitectura Unificada (`LexShield Core`)

1. **Autoridad única de evaluación (`LexShieldCoreEngine` + `ShieldRouter`)**:
   - Se eliminó la bifurcación `LegacyShieldEngine` / `BraveNetworkEngine`, la preferencia dual en `SettingsWindow` (`ChkUseBraveEngine`) y las ramas `Engine=Legacy|Brave` en `ShieldModuleRegistry` y `SitePolicy.Resolve`.
   - **`LexShield Core`** es el único motor de red, `$popup`, `$csp`, cosmética host/genérica y scriptlets estáticos (`lexShield` HostObject COM).
   - **Aislamiento estricto conservado:** YouTube (`MediaShieldRules` + `YouTubeOptimizer`) y Dailymotion (`DailymotionRules` + `DailymotionOptimizer`) se enrutan antes de las reglas generales hacia `LexShieldCoreEngine.EvaluateIsolatedNetwork` y `ShouldBlockIsolatedPopup`.
2. **Bundle `LexBrowser/Assets/shield/lex-shield.lex` (`tools/compile_lex_shield.py` + `tools/lex-extra-rules.txt`)**:
   - Suma `59` reglas propias (`$3p`, `$popup` separado para no interferir navegación directa y brechas de `google.com/pagead/`).
   - Cobertura de los **149** dominios de `AdBlockFilter.BlockedDomains`: **`149 / 149` (100%)** (antes `101 / 149`).
3. **Fuente única de exenciones en 3 niveles (`ShieldExemptions.ResolveExemptionTier`)**:
   - **`TotalExempt`:** `ShieldEnabled=false`, `PaymentOrAuthHosts`, YouTube/Dailymotion aislados y destinos locales (`tv.lex.local`, `pdf.lex.local`).
   - **`NetworkOnly`:** `BigSites` (`Miro`, `Figma`, `Canva`, `Notion`, `ChatGPT`, `Google`, `Twitch`, `Vimeo`, etc.), `CompatMode` o cosmética desactivada con red activa (sin cosmética genérica, overlays ni stubs VAST/IMA; mantiene bloqueo de trackers). Si además `Network=false`, resuelve a `TotalExempt`.
   - **`CosmeticOnly`:** Red desactivada con cosmética activa.
4. **Precedencia unificada de stubs, `204` y `403` + deduplicación JS**:
   - Orden: `@@` excepción (salvo `$important`) → adaptadores YT/DM → `$redirect` declarativo → `AdBlockResources.TryGetUnifiedFallbackStub` (`google-ima.js` MPL-2.0, `noop-vmap.xml`, `noop-vast.xml` acotado a `XHR/Fetch/Other/Media`, `instream-ad-status.js`) → `204 No Content` en telemetría Google/YT → `403 Forbidden`.
   - Eliminados el CSS genérico duplicado (`__lex_cosmetic_shield`) y shims globales (`popMagic`/`ZorDon`/`vastUrl`) de `WebShield.cs`; `WebShield.VideoPlayerDefuserScript` queda solo como respaldo pasivo respetando `Mode=Off`, `Mode=Compat`, `PaymentOrAuthHosts`, `BigSites` y `ModVideoDefuser`.
   - En `LexShieldScriptletEngine.BootstrapScriptTemplate`: hook único de `window.open` (`noPopup` exclusivo sobre `decoyPopup` Proxy `{closed:false}`), `WeakSet __countedEls` compartido para zero doble conteo de overlays/`phantom_anchor`, y protección `isNeverOverlay` (`CANVAS`, `SVG`, `OBJECT`, `EMBED`, `isContentEditable`, `[role=application]`).
5. **Correcciones de subrecursos y manifiestos (`Corrida B1`)**:
   - `DailymotionRules.TryGetRedirectStub` excluye `.m3u8`, `.mpd`, `.ts`, `.m4s`, `.mp4`, `/manifest/video`, `/cdn/manifest/`, `cdndirector` antes de comprobar `"monetization"`.
   - Lectura de `Sec-Fetch-Dest` y `Sec-Fetch-Site` en todos los contextos + `_activeSubframeHosts` en `BrowserTab`/`MainWindow.Tabs.cs` para atribuir correctamente XHRs `no-referrer` de subframes (`cdn.dotanborito.net`).
6. **Experiencia, Copy, Tema y Licencias (`Corrida C`)**:
   - **Migración `Shield.Version` v1..v3 → `v4`** en `SettingsManager` usando constantes explícitas (`LegacyEngineBraveAlias = "Brave"`, `LegacyEngineLegacyAlias = "Legacy"`), conservando preferencias de sitios, `DiagnosticMode`, `EnableCosmeticShield` y **Brave Search** / **Brave Profile Importer**.
   - **Tema predeterminado `Classic` (Original) solo en instalaciones nuevas** (`BrowserSettings.Theme = "Classic"` y fallback de `BrowserThemeManager.NormalizeTheme` a `Classic`), preservando `Dark`/`Light`/`Oscuro`/`Claro` de usuarios existentes.
   - Copy unificado a **"Menos anuncios"** sin adjetivos de ligereza (`"ligero"`, `"ultra-ligero"`, etc.).
   - Nueva pestaña **«📜 Licencias y créditos»** en `SettingsWindow` vinculada a `THIRD_PARTY_NOTICES.txt`.

---

## 2. Comparativa Baseline vs Post-Unificación (Evidencia Medida)

| Métrica / Prueba | Baseline (`master` @ `b021ab2`) | Post-Unificación | Cambio |
| :--- | :--- | :--- | :--- |
| Compilación Release (`LexBrowser.Tests.csproj`) | `0` errores | `0` errores, `0` warnings C# (`NU1900` offline) | Limpia |
| Suite unitaria `dotnet test` | `659/659` (`8 s`) | **`653/653`** (`10 s`, consolidadas + nuevas pruebas v4/3-niveles/JS/Tema/Licencias) | 100% superada |
| Cobertura `AdBlockFilter.BlockedDomains` | `101 / 149` | **`149 / 149` (`Uncovered=0`)** | +48 dominios |
| Carga fría de red (`.lex`, `BaselineProbe audit`) | `38.24 ms` | **`25.51 ms`** (`LazyRes=9.03 ms`, `LazyCos=143.12 ms`) | **-33.3%** |
| Latencia media `ShieldRouter.EvaluateWebResource` (5×100k) | `14,094.4 ns/req` | **`12,247.7 ns/req`** | **-13.1%** |
| Latencia media `BuildDocumentPlanJson` (5×1k) | `0.3020 ms/doc` | **`0.2053 ms/doc`** | **-32.0%** |
| HentaiLA en vivo (`%TEMP%` WebView2, `BaselineProbe live`) | `AllowedAdtngEtahub=0`, `403=9`, XHR `cdn.dotanborito.net` sin atribuir a subframe | **`AllowedAdtngEtahub=0`, `403=9`, `Stub200=1`, `SubBlocked=2`, XHR atribuido `[Subframe:cdn.dotanborito.net]`, video `ready=4, dur=428, jwAdClass=false`** | Resuelto |
| Dailymotion en vivo (`%TEMP%` WebView2, `BaselineProbe live`) | Bug `.m3u8` interceptado como VMAP (`dur=-1`) | **`Stub200=0`, `403=1` (`dmads`), `diag=null` (aislado), cero falsos stubs `.m3u8`** | Resuelto |
| YouTube en vivo (`%TEMP%` WebView2, `BaselineProbe live`) | `204=7`, `Stub200=2`, `0:02/3:33` | **`204=7`, `Stub200=2` (`ad_status.js`), `diag=null` (aislado), `0:02/3:33`, `ytAd=false`** | Íntegro |
| Búsqueda `"brave"` en repo (excl. ref. 3ros/binarios) | `848` líneas en `56` archivos | **`120` líneas clasificadas** (0 en motor/UI AdBlock; solo `THIRD_PARTY_NOTICES`, Brave Search/Importer, migración v4, compilador local y docs históricos) | -85.8% |
| Búsqueda `"ligero\|ultraligero\|..."` en código/UI/web/docs producto | `29` líneas en `19` archivos | **`0` en producto** (`3` solo en cita de `BASELINE_UNIFICACION.md` y aserción negativa en `AuditRegressionTests.cs`) | Eliminado |
| Búsqueda `"cero/sin anuncios\|no ads"` en producto | Presente en NTP / web / descripciones | **`0` en producto** (solo 3 líneas del mock `'No Ads'` en `YouTubeOptimizerTests.cs`) | Cumplido |

---

## 3. Nota Importante sobre Licencias de Terceros (Para Decisión de Release)

Según el inventario técnico en [`docs/INVENTARIO_LICENCIAS_ESCUDO.md`](INVENTARIO_LICENCIAS_ESCUDO.md):
- `LexBrowser.csproj` incrusta `Assets/shield/lex-shield.lex` como `<EmbeddedResource>` en compilaciones single-file.
- Dicho binario compila listas bajo `MPL-2.0` (`brave-lists`), `GPL-3.0 OR CC BY-SA 3.0` (`easylist*`) y `GPL-3.0` (`uBlockOrigin/uAssets` + 24 scriptlets derivados de `uAssets`).
- Antes de publicar un binario propietario cerrado en el siguiente release, decide si: (a) mantienes compatibilidad `GPL-3.0`, (b) entregas las listas externamente/descargables fuera del ensamblado, o (c) depuras los insumos `GPL-3.0` del compilador `tools/compile_lex_shield.py`. La pestaña **Licencias y créditos** y `THIRD_PARTY_NOTICES.txt` ya exponen las atribuciones completas verificadas.

---

## 4. Checklist de Verificación Manual Recomendada (Sesiones Reales / Cuentas del Usuario)

Los flujos autenticados, anti-bot o de lienzos interactivos no deben automatizarse en cuentas reales ni evadir CAPTCHAs; se recomienda validar manualmente en un perfil aislado (`%TEMP%`) o prueba controlada:

- [ ] **Instalación limpia / perfil nuevo:** Confirmar que arranca directamente con **Tema Clásico (Original)** (`#000000` + naranja `#FF6B00`), barra de estado con `LexShield Core` y sin opción de selector dual en Ajustes.
- [ ] **Actualización sobre perfil existente (`Dark` / `Light`):** Verificar que un `settings.json` previo en Oscuro o Claro mantiene intacto su tema, sitios pausados y buscador guardado tras migrar a `Shield.Version = 4`.
- [ ] **Ajustes → `📜 Licencias y créditos`:** Comprobar lectura completa de `THIRD_PARTY_NOTICES.txt` y botón **«Abrir archivo»**.
- [ ] **Google Search, Google Lens y Visión General IA:** Buscar tras resolver CAPTCHA si aplica y subir una imagen en Lens (confirmar streaming sin bloqueos de red).
- [ ] **Google / Microsoft OAuth / SSO y Pasarelas de Pago (`PayPal`, `Stripe`, `MercadoPago`):** Iniciar sesión en popup OAuth y probar checkout de prueba verificando `TotalExempt`.
- [ ] **Microsoft 365 (`office.com`, `live.com`) y Web Apps de Lienzo (`Miro`, `Figma`, `Canva`, `ChatGPT`):** Verificar edición fluida en `<canvas>`, arrastrar/soltar, scroll y menús sin interferencia de overlays ni cosmética (`NetworkOnly`).
- [ ] **Buscador e Importador Brave:** Confirmar que seleccionar **Brave Search** funciona en Omnibox/NTP y el asistente de importación detecta perfiles locales si existen.
