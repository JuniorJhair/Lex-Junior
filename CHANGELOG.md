# Changelog — Lex Junior Browser

Todos los cambios notables de **Lex Junior Browser** se documentan en este archivo usando [Semantic Versioning](https://semver.org/).

## [2.7.0] - 2026-11-18

### Novedades y mejoras
- **Lex PDF Studio con soporte para PDFs locales y virtuales (`http://` y `https://`)**:
  - Nuevo canal interno autenticado `api/load-remote-pdf` en `https://pdf.lex.local/viewer.html` con lista blanca por sesión (`AuthorizeRemoteUrl`), reenvío de cookies de WebView2 acotado por salto (`CookieManager.GetCookiesAsync`), hasta 8 redirecciones seguras con protección anti-SSRF y DNS pinning (`TvUrlGuard.CreatePinnedHttpClient`), validación de firma mágica `%PDF-`, límite de 100 MB y rechazo explícito (`415`) de portales HTML de inicio de sesión.
  - Conservación de `AppState.pdfBytes` en memoria (pasando copia `.slice(0)` al worker de PDF.js) para hacer zoom, anotar, resaltar, estampar firma digital y guardar PDFs virtuales sin errores de CORS, más tarjeta de estado con botones **Reintentar** y **Abrir en modo navegador** (`open_raw_pdf`).
- **Botón y adelanto oficial de `Lex Junior Class` (`Disponible en la próxima actualización`)**:
  - Incorporación del botón **🎓 Lex Class (PRONTO)** en la barra de navegación principal, acceso directo en Nueva Pestaña y soporte para `lex://class` / `https://class.lex.local/index.html`, abriendo la ventana ilustrada `LexClassComingSoonDialog` inspirada en el logotipo oficial de Lex Junior.
- **Nuevo diálogo de actualización con formato rico y desplazamiento (`UpdatePromptDialog`)**:
  - Sustitución del antiguo `MessageBox.Show` por una ventana WPF tematizada con `ScrollViewer`, insignias comparativas de versión (`v2.6.0 ➔ v2.7.0`), barra de progreso de descarga en vivo y motor de renderizado Markdown (`ParseReleaseNotesBlocks`) que convierte encabezados, listas, código inline y texto en **negrita** sin mostrar asteriscos `**` crudos.
- **Perfiles de Privacidad y Seguridad (`Perfil A`, `Perfil B`, `Perfil C` y `Personalizado`)**:
  - Nuevo asistente de bienvenida (`PrivacyProfileSetupWindow`) y matriz comparativa en Configuración (`PrivacyProfileMatrixControl`) que gobiernan 7 políticas con conexión de red (`EnableSmartScreen`, `EnableClientSidePhishingDetection`, `EnableLinkFaviconDownload`, `EnableDirectFaviconIco`, `EnableExternalFaviconService`, `EnableSearchSuggestions`, `AutoUpdateCheckMode`).
  - Nueva compuerta de red de arranque (`FaviconService.SetStartupNetworkGate`) que impide descargas de favicons o tráfico auxiliar antes de completar el desbloqueo por PIN y la elección inicial de perfil.
- **Instalador expandido sin recortes visuales (`InstallerWindow`)**:
  - Ampliación del margen del encabezado y plantilla `ControlTemplate` dedicada (`32×32 px`, `Padding="0"`, `Panel.ZIndex="10"`) para el botón de cierre (`✕`) de modo que siempre se visualice completo y nítido.
- **Recuperación inteligente de `settings.json` y protección Anti-Downgrade**:
  - Rescate propiedad por propiedad ante archivos `settings.json` dañados con respaldo único `.corrupt.<timestamp>.bak`, marcador `vault.lock` para preservar el bloqueo PIN y diálogo `VersionDowngradeDialog` que evita que ejecutables antiguos sobrescriban perfiles de versiones más recientes.
- **Endurecimiento del actualizador, bóveda DPAPI y gestor de descargas**:
  - Verificación criptográfica **SHA-256** y tamaño de `LexJuniorSetup.exe` desde GitHub Releases antes de ejecutar la actualización en `%LOCALAPPDATA%\LexBrowser\Updates`.
  - Autocompletado de contraseñas restringido a `IsSameExactOrWwwHost` (bloqueando fugas entre subdominios de hosting compartido como `github.io`, `vercel.app`, `pages.dev`, `netlify.app`) y sincronización fluida de descargas activas cada `350 ms`.
- **Sitio web oficial renovado con arquitectura multi-ruta (`website/`)**:
  - Navegación estructurada por rutas (`#/inicio`, `#/funciones`, `#/lex-class`, `#/documentacion`, `#/faq`, `#/novedades`), simulador interactivo actualizado con la vista de **Lex Junior Class**, manual técnico completo y buscador interactivo de Preguntas y Respuestas.

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
