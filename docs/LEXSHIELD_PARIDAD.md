# Paridad del Motor Declarativo vs Lex (Histórico Pre-Unificación)

> **Nota histórica:** Documento de análisis previo a la unificación en `LexShield Core` (`feature/escudo-unificado`). El estado vigente se documenta en [`UNIFICACION_ESCUDO.md`](UNIFICACION_ESCUDO.md) y [`REPORTE_UNIFICACION_FINAL.md`](REPORTE_UNIFICACION_FINAL.md).

Referencia local inspeccionada: `COMO BLOQUEA BRAVE/brave-core` (`ad_block_engine.cc`, `adblock_stub_response.cc`), `COMO BLOQUEA BRAVE/adblock-rust-master`, `COMO BLOQUEA BRAVE/adblock-lists-master` y `COMO BLOQUEA BRAVE/adblock-resources-master`.

## 1. Artefacto compilado actual (`LexBrowser/Assets/shield/brave-shield.lex`)

Compilado con `tools/compile_brave_shield.py` (deduplicando líneas crudas de 17 listas: `brave-main-list.txt`, `coin-miners.txt`, `easylist.txt`, `easyprivacy.txt`, `easylistspanish.txt`, `uBlockOrigin/{filters,badware,resource-abuse,unbreak}.txt` — suprimiendo solo 1 excepción `viki.com` conflictiva con `redirect=google-ima.js` —, `brave-unbreak.txt`, `brave-firstparty.txt`, `brave-firstparty-regional.txt`, `brave-cookie-specific.txt`, `brave-specific.txt`, `brave-sugarcoat.txt` + 2 catálogos de recursos JSON):

- **Reglas crudas deduplicadas:** 203,654 | **Tamaño `.lex` (`LEXSHLD2`):** 3,078,746 bytes (2.94 MB)
- **Red:** 115,030 `pureHosts` | 31,262 `patternRules` | 456 `regexRules` | 230 `cspRules` | 35 `fallbackRules`
- **Cosmética y Scriptlets:** 42,518 `cosmeticHosts` | 27,636 `cosmeticUniquePayloads` | 9,196 `simpleClasses` | 4,376 `simpleIds` | 620 `miscGenericHide` | 223 `resources`

## 2. Matriz de paridad: Brave vs Lex

| Mecanismo | Brave (`brave-core` + `adblock-rust`) | Lex (`WebView2` + `Brave*Engine`) | Brecha | ¿Factible en WebView2? |
| :--- | :--- | :--- | :--- | :--- |
| Red / Subrecursos (`WebResourceRequested`) | NetworkDelegate / URLLoaderThrottle C++ | `WebResourceRequested` + `BraveNetworkEngine` + stubs 200/204/403 | Paridad en bloqueo/redirect/token/regex; WebSocket y algunas peticiones internas tienen menor alcance | Sí (cubierto para HTTP/S en `WebResourceRequested`) |
| Navegación de subframes (`iframe`) | Interceptación C++ antes de crear navegación de frame | `frame.NavigationStarting` evalúa `ShieldRouter.EvaluateWebResource` y cancela + `SUBFRAME_BLOCKED` | Cubre `iframe` HTTP/S antes de cargar URL; falta bloqueo de blobs sin URL HTTP | Sí (implementado) |
| Cosmética host & genérica + estilos | Inyección temprana Blink C++ + stylesheet user-origin | `BraveBridgeHostObject.GetDocumentPlanJson` síncrono + `QueryCosmeticsJson` + `<style>` | Stylesheet de autor DOM vs user-agent stylesheet; páginas ya cargadas conservan su plan previo | Sí (implementado) |
| Scriptlets (`+js(...)`) | Inyección C++ directa en el mundo principal antes de scripts | Registro estático compilado (`__getLexReg()` / `__runStaticScriptlet`) desde `BootstrapScript`, **cero `new Function`** | Respeta CSP `unsafe-eval`; omitidos 212 `cos_unsupported_skipped` (`##^`, `#%#`, `#$#`) y recursos `trusted-*` privilegiados | Parcial (CSP-safe sin `trusted-*`) |
| Popups / `NewWindowRequested` | Browser process popup blocker + `$popup` | `NewWindowRequested` + reglas `$popup` + anti-tab-under + hook `window.open` | Cubre ventanas nuevas y popunders declarativos; heurística adicional de gesto de usuario | Sí |
| `$csp=` en documento | Inyecta cabecera HTTP `Content-Security-Policy` en respuesta | `<meta http-equiv="Content-Security-Policy">` insertada antes de `<head>` (230 reglas) | `<meta>` no soporta `frame-ancestors` ni `report-uri` como cabecera HTTP real (230 reglas activas) | Parcial |
| `$removeparam` / `$urlskip` (280 / 193 reglas) | Reescribe URL en vuelo en capas de red C++ | Solo limpieza de navegación principal (`SitePolicy.TryStripTrackingParameters`); omitidas en compilador `.lex` | `WebResourceRequested` no soporta redirección silenciosa arbitraria de subrecursos sin CORS | No (subrecursos); Parcial (navegación) |
| `$header=` / `$cname` (51 / 29 reglas) | Inspección de respuesta HTTP y resolución DNS CNAME | Omitidas en compilador (`opt_header_skipped=51`, `opt_cname_skipped=29`) | `WebResourceRequested` evalúa antes de DNS/respuesta HTTP y WebView2 no expone resolución DNS CNAME previa | Parcial (`$header`); No (`$cname`) |
| HTML filtering `##^` / scriptlet `#$#` / `trusted-*` | Motor nativo HTML/response body rewrite (212 reglas omitidas) | Omitidas en compilador (`cos_unsupported_skipped=212`) | WebView2 no expone stream mutator nativo de cuerpo HTML antes del parser | No |
| Ephemeral / Farbling V8 (1PES, Canvas/WebGL) | Parches nativos en Chromium/Blink/V8 | No presente en motor WebView2 | Requiere parches de código fuente Chromium | No |

## 3. Causas de brecha diagnosticadas y cerradas en este ciclo

1. **Rutas obsoletas en `compile_brave_shield.py`:** Apuntaba a `adblock-rust-master/data/brave` tras mover los repos a `COMO BLOQUEA BRAVE/`, impidiendo recompilar listas actualizadas; ahora compila desde `COMO BLOQUEA BRAVE` sumando `uBlockOrigin`, `brave-firstparty*`, `brave-cookie-specific`, `brave-specific`, `brave-sugarcoat`, `easylistspanish` y los 7 recursos de consentimiento de `adblock-resources-master`.
2. **Subframes no cancelados en `frame.NavigationStarting`:** `a.adtng.com` (presente como `||adtng.com^` en `brave-main-list.txt`) cargaba en iframes porque solo se registraba `SUBFRAME_NAVIGATION_STARTING`; ahora `frame.NavigationStarting` cancela navegación de subframes bloqueados y registra `SUBFRAME_BLOCKED`.
3. **Eliminación de `new Function`:** `BootstrapScriptTemplate` ejecutaba `(new Function(__plan.scriptlets))()`. Se reemplazó por despacho estático O(1) `__runStaticScriptlet(n, a)` con `scriptletCalls` JSON y caché `ConditionalWeakTable<BraveResourceStore, string>`.
