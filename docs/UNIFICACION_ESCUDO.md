# Unificación de Motores en `LexShield Core` (`UNIFICACION_ESCUDO.md`)

**Base:** `master` @ `b021ab2` (`backup-pre-unificacion-20261115-2227`) · **Motor único:** `LexShield Core` (puente `lexShield` intacto).  
**Principio rector:** Una sola autoridad de evaluación, una sola configuración (`Shield.Version=4`), una sola fuente de exenciones de 3 niveles y una única observabilidad (`ShieldLogger` + `ShieldModuleRegistry`).

## 1. Inventario y Decisiones de Fusión

| Mecanismo | Origen | Decisión | Motivo y Evidencia |
| :--- | :--- | :--- | :--- |
| Tabla fija `BlockedDomains` (149 hosts) | Legacy (`AdBlockFilter`) | **Conservar migrado a `"extra"`** | Auditado: **101/149** ya cubiertos en `.lex`; **48** faltaban (`streamtp10.com`, `adtng.net`, `tjassets.com`, `googlesyndication.com`, etc.). Pasar redes puras como `\|\|host^$3p` (y apuestas/adultos como `$3p,popup`) para no bloquear navegación directa. |
| `GenericAdUrlPatterns` (36 subcadenas) | Legacy (`AdBlockFilter`) | **Depurar y eliminar subcadenas** | Solo 2/36 coincidían como subcadena cruda; 4 ya viven en `MediaShieldRules`; las válidas (`cloud3-easiness925.workers.dev`, `pr_advertising_`, `ad-provider.js`, `slider.php`, `vast.php`) se pasan acotadas (`$3p`/`$script`); se eliminan ambiguas (`click.php?`, `link?z=`, `adformat=`). |
| `SuspiciousPopunderTLDs` + `HostKeywords` | Legacy (`AdBlockFilter`) | **Conservar ambos (unificados)** | Ya integrado como etapa 4 de `BraveNetworkEngine.ShouldBlockPopup` tras `$popup` declarativo, bloqueando dominio sin filtrar por reglas de red generales. |
| Facebook tracking (`/tr`, `/signals`, `fbevents.js`) | Legacy (`AdBlockFilter`) | **Conservar uno (declarativo)** | Cubierto por EasyPrivacy en `.lex`; OAuth (`sdk.js`) preservado; auditar fallback mínimo solo si falta regla. |
| Google telemetría (`gen_204`, `/log`) vs Lens/AI | Ambos | **Conservar ambos con regla por endpoint** | Legacy permitía todo en origen Google; Brave bloquea `/gen_204?` y `\|\|google.com/log^` (`accounts.google.com/gsi/log` permitido). En vivo `lens.google.com/v3/upload`, `/complete/search` y `/async/fol` dieron `ALLOW`; silenciar telemetría con **204 No Content**, y reintroducir regla de red `google.com/pagead/` que faltaba en `.lex`. |
| Stubs VAST/VMAP/JS/GIF (`AdBlockResources`) | Legacy | **Conservar migrado + precedencia** | Auditado: hoy en modo Brave, `ima3.js`, `adtng.com`, `magsrv.com`, `exoclick.com`, `?output=vast` y `?format=vmap` NO devuelven stub `200` (reciben `403` o `Allow`). Mover shims útiles a etapa ordenada de stubs tras reglas con `$redirect`. |
| `GoogleImaScript` (`ima3.js`, MPL-2.0) | Ambos (`AdBlockResources` / `BraveResourceStore`) | **Conservar uno (propietario único)** | Unificar bajo etapa única `ResolveStubOrRedirect` tras excepciones y `BigSites`/`PaymentOrAuth` para evitar doble stub e hilos competidores. |
| Motor de red (`PureHosts`, `PatternRules`, `Regex`, `CSP`) | Declarativo | **Conservar** | 115,030 hosts + 31,262 patrones + 230 `$csp` + 456 regex; 14.1 µs/req y traceable por regla exacta en log. |
| Cosmética (`CosmeticEngine`) + `ScriptletEngine` | Declarativo | **Conservar** | 42,518 hosts cosméticos + lookup O(1) bajo demanda + `$generichide` + `##+js(...)` compilados estáticamente sin `eval`. |
| `WebShield` shims (`popMagic`, `ZorDon`, `vastUrl`) y CSS (`__lex_cosmetic_shield`) | Legacy (`WebShield`) | **Eliminar** | Duplican y chocan con `##+js(...)` y `HostCss`/`QueryGenericCss` (ignorando `$generichide` en iframes). |
| `WebShield` capas fantasma, clickguard (`phantom_anchor`), `hasTrapTargetBeneath`, `window.open` señuelo Proxy | Legacy (`WebShield`) | **Conservar como capa complementaria** | Declarativo solo detecta overlays `>=85% viewport` `z>=9999` en `mousedown` y anula `window.open=null` en `$csp=sandbox`. Conservar `classifyGhost` (`hard`/`soft`), click pass-through y proxy `{closed:false}` **sujetos a `!ghide && !isCompat`**, un solo hook a `window.open` y deduplicado. |
| `VideoPlayerDefuser` (JW/Fluid/Video.js pasivo) | Legacy (`WebShield`) | **Conservar solo como respaldo opt-in / pasivo** | Respalda recuperación si bloqueos de VAST pausan JW Player (`adError`/`adBlock -> play()`), deshabilitado por defecto salvo opt-in o embed autorizado, sin tocar YT/DM. |
| `MediaShieldRules` + `YouTubeOptimizer` | Propio | **Conservar aislado** | Corpus vivo YouTube: `0` ads, `204=7`, `Stub200=2`, video `0:02/3:33` fluido. No mezclar jamás con reglas `.lex`. |
| `DailymotionRules` + `DailymotionOptimizer` | Propio | **Conservar aislado (corregir bug `.m3u8`)** | **Hallazgo en vivo:** `DailymotionRules.cs:121` usa `full.Contains("monetization")` y reemplazó `dmxleo.dailymotion.com/cdn/manifest/video/x8t73c2.m3u8?...` con XML vacío (`dur=-1`). Excluir explícitamente `.m3u8` / `/manifest/video` antes de Stub VMAP. |
| `CookieBannerShield` | Propio | **Conservar** | CSS inmediato no interactivo, exento en banca/OAuth/YT; mantener integrado en pipeline y registro único. |
| `ShieldExemptions` / `TrustedRootDomains` / `ShieldIsolation` | Ambos | **Unificar en fuente única de 3 niveles** | Unificar tablas duplicadas (`TrustedRootDomains` vs `BigSites` vs hashes internos) con niveles explícitos. |
| `ShieldModuleRegistry` + `ShieldRouter` + `ShieldLogger` | Infraestructura | **Conservar y unificar** | Quitar `EngineLegacy` vs `EngineBrave` y ramas duales (`ModVideoDefuser` solo legacy); un solo contador por evento. |

## 2. Etapas Ordenadas, Precedencia de Conflictos y Fuente de Verdad en `LexShield Core`

1. **Fuente única de exenciones (3 niveles)**:
   - **Nivel 1 — `Exento Total`** (ni red ni cosmética ni defuser): esquemas no-HTTP/locales (`pdf.lex.local`, `tv.lex.local`), **`PaymentOrAuthHosts`** (pagos, bancos, OAuth, CAPTCHA) y sitios puestos en `Mode=Off` o con `ShieldEnabled=false`.
   - **Nivel 2 — `Solo Red` (sin cosmética/overlays ni stubs intrusivos)**: **`BigSites`** (Miro, Figma, Canva, Notion, OpenAI, ChatGPT, Google App/Search, etc.), sitios en `Mode=Compat`, o sitios donde reglas `$generichide` / `Cosmetic=false` apagan la cosmética genérica y el detector anti-overlay. En `Solo Red` se bloquea telemetría/trackers de terceros puros pero jamás scripts funcionales ni stubs VAST/IMA sobre plataformas productivas.
   - **Nivel 3 — `Solo Cosmética` (red intacta)**: perfiles donde el usuario apague `ModNetwork` dejando `ModCosmetic`/`CookieBanners` activos.
   *(Nota de aislamiento: **YouTube** y **Dailymotion** salen del motor general antes de reglas `.lex` hacia sus adaptadores aislados.)*

2. **Pipeline único y orden de evaluación**:
   `1. Exenciones (3 niveles)` → `2. Adaptadores aislados (YT / DM)` → `3. Reglas declarativas de red + lista "extra"` → `4. Stubs / Redirects (precedencia abajo)` → `5. Popups ($popup declarativo → sandbox → heurística WebShield/Filter, 1 solo hook window.open)` → `6. Cosmética y scriptlets declarativos (con CSP)` → `7. Heurísticas complementarias WebShield (overlays/clickguard si !ghide, defuser pasivo si activado)`.

3. **Precedencia de Red / Stubs (`200 VAST` vs `204` vs `403` vs `Allow`)**:
   1. Excepción `@@` (salvo `$important` ganador).
   2. Adaptadores aislados YT / DM (`WantsEmptyJson` → `200 {}`; `DailymotionRules` VMAP/canary solo tras verificar que NO es `.m3u8` / `/manifest/video`).
   3. Dominio bloqueado con regla `$redirect` (del bundle `.lex` o mapeo IMA/VAST `"extra"`) → **`200 OK` Stub** si `policy.Stubs` (y `policy.ImaStub` para `ima3.js`) están activos y no es `BigSites` ni `PaymentOrAuth`; caso contrario cae a `403`. *(Propietario único de IMA: un solo punto de resolución tras evaluación declarativa, eliminando el fallback duplicado).*
   4. Telemetría Google (`AdBlockFilter.IsGoogleDomain(originHost)`) o YT/Google ad telemetry bloqueada sin `$redirect` → **`204 No Content`** (evita errores de red en Search/IA Overview).
   5. Etiquetas de video genéricas (`?output=vast`, `?format=vmap`, `adtng`/`magsrv`/`exoclick` en contexto `XHR/Fetch/Other/Media`, nunca `Document`) bloqueadas sin regla `$redirect` específica cuando `policy.Stubs=true` y jugador tercero lo requiere → **`200 OK` `<VAST/>`/`<VMAP/>`** si la lista declarativa o contrato de reproductor pide stub; scripts/iframes publicitarios (`vast.js`, `p.js`, subframes `adtng.com`) → **`403 Forbidden`** (verificado en HentaiLA: bloquear `ssl.p.jwpcdn.com/player/v/*/vast.js` con `403` evitó el pre-roll sin congelar el video).
   6. Bloqueo duro normal (`pureHost` / `pattern` / `$important`) → **`403 Forbidden`** (o cancelar `Frame.NavigationStarting` en subframes).

4. **Correcciones arquitectónicas obligatorias para Corrida B**:
   - **Subframes con `Referrer-Policy: no-referrer`** (ej. `cdn.dotanborito.net` en HentaiLA): en `MainWindow.Tabs.cs:899-904` leer `Sec-Fetch-Dest` y `Sec-Fetch-Site` en todos los contextos (no solo `Document`) y cruzar `Origin` con `tab.ActiveFrames` en `ClassifyWebResourceRequest` para evitar atribuir XHRs de un iframe aislado como `Origin: hentaila.com` (`Subframe=False`).
   - **Propietario único en JS**: un solo wrapper de `window.open` (delegando `$csp=sandbox` y heurística proxy `{closed:false}` sin sobreescribirse entre módulos) y deduplicación por `WeakSet`/`__lexShieldBootstrapped` en top y subframes para garantizar **0 doble conteo** en `ShieldLogger` y `ShieldModuleRegistry`.
   - **Migración v3 → v4**: una única constante `LegacyEngineBraveToken = "Brave"` (explícita, sin ofuscar) en el migrador; retiro de `ChkUseBraveEngine` en `SettingsWindow.xaml` manteniendo intactos **Brave Search** (`DefaultSearchEngine="Brave"`) y el **importador de perfiles Brave**.
