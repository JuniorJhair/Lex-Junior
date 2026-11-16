# Changelog — Lex Junior Browser

Todos los cambios notables de **Lex Junior Browser** se documentan en este archivo usando [Semantic Versioning](https://semver.org/).

## [2.4.0] - 2026-11-15

### Novedades y mejoras
- **Motor declarativo estilo Brave por defecto**: El motor declarativo basado en `brave-shield.lex` (`BraveNetworkEngine`, `BraveCosmeticEngine`, `BraveScriptletEngine`, `BraveResourceStore`) pasa a ser el predeterminado en perfiles nuevos (`ShieldSettings.Engine = "Brave"`, esquema `v3`), con migración automática **de una sola vez** desde `Legacy` (`EngineMigratedToBrave`) que respeta la elección manual posterior del usuario y sus demás preferencias.
- **Aislamiento estricto garantizado**: YouTube y Dailymotion permanecen exclusivamente en su ruta aislada (`ShieldIsolation` + `LegacyShieldEngine.EvaluateIsolatedNetwork` + `YouTubeOptimizer` / `DailymotionOptimizer`), sin contaminación de reglas o scriptlets generales de Brave.
- **Defaults alineados de LexShield**: `Subframes` activado por defecto (`true`), `DiagnosticMode` desactivado (`false`), adaptadores YT/DM activos (`true`) y `VideoDefuser` marcado claramente como módulo exclusivo de Motor Legacy (deshabilitado en Ajustes y popup cuando Brave está activo).
- **Empaquetado single-file de `brave-shield.lex`**: `LexBrowser\Assets\shield\brave-shield.lex` incluido como `EmbeddedResource` además de copiarse al directorio de salida, asegurando disponibilidad offline dentro del ejecutable único (`LexJuniorSetup.exe`).
- **Trazabilidad y diagnóstico de red**: Registro de `REQUEST_ALLOWED` en `ShieldLogger` bajo modo diagnóstico y emisión obligatoria ante condición `data == null` (fail-open explícito y auditable).
- **Popup de LexShield y registro centralizado (`ShieldModuleRegistry`)**: Fuente única de verdad para popup, contadores, Ajustes y encabezados de diagnóstico; el toggle maestro del sitio pausa/reactiva sin borrar módulos individuales deshabilitados y responde correctamente al clic normal.
- **Descargas integradas**: Supresión del flyout nativo de WebView2 (`e.Handled = true` + cierre preventivo del diálogo por defecto) y transición inmediata al estado `"Completado"` al finalizar la descarga.

### Limitaciones conocidas (entorno WebView2)
- Opciones de filtro no soportadas en la capa `WebResourceRequested` de WebView2 y excluidas en compilación/evaluación: `$removeparam`, `$header`, `$cname`, `$urlskip`, `$ipaddress`, `$tag`, `$strict1p`, `$strict3p`, `$match-case`.
- Sintaxis cosméticas/scriptlet fuera de alcance en navegador embebido: filtrado HTML de respuesta (`##^`), sintaxis propietarias de AdGuard (`#%#`, `#$#`), ubicaciones regex y scriptlets privilegiados `trusted-*` omitidos en `brave-shield.lex` (ver `docs/BRAVE_PARIDAD.md`).

## [2.3.0]
- Versión base previa con Motor Legacy por defecto, soporte experimental de motor Brave por activación manual, Lex TV v2, Lex PDF Studio y mejoras generales de estabilidad.
