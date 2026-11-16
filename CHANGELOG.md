# Changelog — Lex Junior Browser

Todos los cambios notables de **Lex Junior Browser** se documentan en este archivo usando [Semantic Versioning](https://semver.org/).

## [2.5.0] - 2026-11-16

### Novedades y mejoras
- **Motor unificado `LexShield Core` (`Shield.Version = 4`)**: Fusión de evaluación declarativa (`LexShieldCoreEngine`, `LexShieldCosmeticEngine`, `LexShieldScriptletEngine`, `LexShieldResourceStore`) y reglas complementarias bajo una sola autoridad de evaluación y puente `lexShield`, eliminando el selector dual de motor y migrando configuraciones previas `v1..v3` automáticamente.
- **Bundle `lex-shield.lex` con reglas `"extra"`**: Reemplaza `brave-shield.lex` sumando reglas propias migradas en `$3p`/`$popup` y precedencia unificada de stubs (`$redirect` → IMA/VAST → `204`/`403`) con exenciones en 3 niveles (`TotalExempt`, `NetworkOnly`, `CosmeticOnly`), incrustado como `EmbeddedResource` en `LexJuniorSetup.exe`.
- **Aislamiento estricto garantizado**: YouTube y Dailymotion permanecen exclusivamente en su ruta aislada (`ShieldIsolation` + `LexShieldCoreEngine.EvaluateIsolatedNetwork` + `YouTubeOptimizer` / `DailymotionOptimizer`), corrigiendo la exclusión de `.m3u8`/`/manifest/video` en `DailymotionRules` y la atribución de subframes sin `Referer`.
- **Tema Clásico (Original) predeterminado en instalaciones nuevas y mejoras de Tema Claro**: Los perfiles nuevos inician con `Theme = "Classic"` respetando íntegramente `Dark`/`Light` en actualizaciones; corregido el contraste en Tema Claro en píldoras de barra (`0 bloqueados`, `Sin telemetría`, `100%`, `Multimedia`, botón `+`/`Anclar página` e ícono `X` de Nueva Pestaña) y tematización completa (`Claro` / `Oscuro` / `Clásico`) en `Configuración` e `Historial`, incluyendo el estado visual de `✓ Ya es predeterminado` y la alineación de copy (`Cero telemetría · Menos anuncios · Privado`).
- **Historial UTF-8 sin mojibake**: Normalización segura en `HistoryStore` al cargar (`history.json`) y registrar títulos nuevos/actualizados para reparar secuencias UTF-8 decodificadas erróneamente como Latin-1/Windows-1252 (`Ãºltimo` → `último`).
- **Licencias, créditos y neutralización de textos**: Nueva pestaña **Licencias y créditos** (`THIRD_PARTY_NOTICES.txt`) en Ajustes y neutralización de afirmaciones no medidas en interfaz, web y documentación.

### Limitaciones conocidas (entorno WebView2)
- Opciones de filtro no soportadas en la capa `WebResourceRequested` de WebView2 y excluidas en compilación/evaluación: `$removeparam`, `$header`, `$cname`, `$urlskip`, `$ipaddress`, `$tag`, `$strict1p`, `$strict3p`, `$match-case`.
- Sintaxis cosméticas/scriptlet fuera de alcance en navegador embebido: filtrado HTML de respuesta (`##^`), sintaxis propietarias de AdGuard (`#%#`, `#$#`), ubicaciones regex y scriptlets privilegiados `trusted-*` omitidos en `lex-shield.lex` (ver `docs/LEXSHIELD_PARIDAD.md`).

## [2.4.0] - 2026-11-15

### Novedades y mejoras
- **Motor declarativo por defecto (`v2.4.0`)**: Activación predeterminada del motor declarativo (`v3`), migración única de configuraciones previas, aislamiento de YouTube/Dailymotion, trazabilidad `REQUEST_ALLOWED` (incluyendo fail-open `data == null`), registro centralizado `ShieldModuleRegistry` y supresión del flyout nativo de WebView2 en descargas.

## [2.3.0]
- Versión base previa con motor dual, Lex TV v2, Lex PDF Studio y mejoras generales de estabilidad.
