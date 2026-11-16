# Changelog — Lex Junior Browser

Todos los cambios notables de **Lex Junior Browser** se documentan en este archivo usando [Semantic Versioning](https://semver.org/).

## [2.6.0] - 2026-11-16

### Novedades y mejoras
- **Identidad visual oficial de Lex Junior Browser**: Sustitución integral del icono y logotipo (`Logo.png`) en la aplicación (`app.ico`, `logo.png`, `logo_web.png`), recursos locales de Lex TV (`favicon.ico`/`favicon.png`) y sitio web oficial (`website/assets/`).
- **Sitio web renovado (Oscuro Morado + Original Naranja)**: Nueva paleta combinada del tema **Oscuro** (morado/índigo) y **Original/Clásico** (naranja sobre negro), con **Tema Clásico (Original)** como vista principal del navegador interactivo en la web y selector en tiempo real para alternar entre **Original (Clásico)**, **Oscuro** y **Claro**.
- **Atribución oficial y corrección de `THIRD_PARTY_NOTICES.txt` en compilación única**: Incrustación de `THIRD_PARTY_NOTICES.txt` como `EmbeddedResource` en `LexJuniorSetup.exe` con créditos oficiales (`Copyright © 2026 Junior Pacherrez`), texto legal completo de licencias y extracción/apertura garantizada desde **Configuración → Licencias y créditos**.
- **Funciones oficiales de diagnóstico y logs en instalación**: Eliminada la etiqueta `(Temporal en esta sesión)` en `Modo Diagnóstico Verboso de LexShield`, renombrado `Adaptador de Google IMA SDK y Stubs VAST/VMAP`, y exportación de registros (`Ctrl + Shift + L` / Diagnóstico por pestaña) dirigida a la subcarpeta `Logs\LexJunior_ShieldLog.txt` dentro del directorio del navegador (sin dejar archivos en el Escritorio).

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
