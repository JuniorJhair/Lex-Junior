# Changelog — Lex Junior Browser

Todos los cambios notables de **Lex Junior Browser** se documentan en este archivo usando [Semantic Versioning](https://semver.org/).

## [2.4.0] - 2026-11-15

### Novedades y mejoras
- **Motor unificado `LexShield Core` (`Shield.Version = 4`)**: Fusión de evaluación declarativa (`LexShieldCoreEngine`, `LexShieldCosmeticEngine`, `LexShieldScriptletEngine`, `LexShieldResourceStore`) y reglas complementarias migradas desde `AdBlockFilter`/`AdBlockResources`/`WebShield` bajo una sola autoridad de evaluación y puente `lexShield`, eliminando el selector dual de motor y migrando configuraciones previas `v1..v3` automáticamente.
- **Bundle `lex-shield.lex` con reglas `"extra"`**: Reemplaza `brave-shield.lex` sumando reglas propias migradas en `$3p`/`$popup` (149/149 dominios cubiertos) y precedencia unificada de stubs (`$redirect` → IMA/VAST → `204`/`403`) con exenciones en 3 niveles (`TotalExempt`, `NetworkOnly`, `CosmeticOnly`).
- **Aislamiento estricto garantizado**: YouTube y Dailymotion permanecen exclusivamente en su ruta aislada (`ShieldIsolation` + `LexShieldCoreEngine.EvaluateIsolatedNetwork` + `YouTubeOptimizer` / `DailymotionOptimizer`), corrigiendo además la exclusión de `.m3u8`/`/manifest/video` en `DailymotionRules` y la atribución de subframes con `no-referrer`.
- **Tema Clásico (Original) predeterminado en instalaciones nuevas**: Los perfiles nuevos inician con `Theme = "Classic"` respetando íntegramente `Dark`/`Light` en actualizaciones de usuarios existentes, junto con pestaña de **Licencias y créditos** (`THIRD_PARTY_NOTICES.txt`) en Ajustes.
- **Trazabilidad y diagnóstico de red**: Registro de `REQUEST_ALLOWED` en `ShieldLogger` bajo modo diagnóstico, deduplicación consecutiva de entradas y emisión obligatoria ante condición `data == null` (fail-open explícito y auditable).
- **Popup de LexShield y registro centralizado (`ShieldModuleRegistry`)**: Fuente única de verdad para popup, contadores, Ajustes y encabezados de diagnóstico; desarmador pasivo opcional de reproductor disponible bajo un único flujo.
- **Empaquetado single-file y descargas integradas**: `LexBrowser\Assets\shield\lex-shield.lex` incluido como `EmbeddedResource` y supresión del flyout nativo de WebView2 en descargas.

### Limitaciones conocidas (entorno WebView2)
- Opciones de filtro no soportadas en la capa `WebResourceRequested` de WebView2 y excluidas en compilación/evaluación: `$removeparam`, `$header`, `$cname`, `$urlskip`, `$ipaddress`, `$tag`, `$strict1p`, `$strict3p`, `$match-case`.
- Sintaxis cosméticas/scriptlet fuera de alcance en navegador embebido: filtrado HTML de respuesta (`##^`), sintaxis propietarias de AdGuard (`#%#`, `#$#`), ubicaciones regex y scriptlets privilegiados `trusted-*` omitidos en `lex-shield.lex`.

## [2.3.0]
- Versión base previa con motor Legacy/declarativo dual, Lex TV v2, Lex PDF Studio y mejoras generales de estabilidad.
