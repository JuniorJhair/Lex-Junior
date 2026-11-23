# Changelog — Lex Junior Browser

Todos los cambios notables de **Lex Junior Browser** se documentan en este archivo usando [Semantic Versioning](https://semver.org/).

## [2.8.1] - 2026-11-22

### Novedades y mejoras
- **Matemáticas interactivas mejoradas (Niveles 1 a 5 en Lex Junior Class)**:
  - **Cuadrículas interactivas `DM | UM | C | D | U`** para sumas, restas y multiplicaciones de varias cifras con fila superior para **llevadas** (`+1`) y **tachados de préstamo** (`2 | 14 | 9 | 10`), productos parciales en multiplicación por 2 cifras, botón conmutativo `⇄ Cambiar orden` en sumas, indicador de orden fijo en restas y explicaciones columna por columna (incluida estrategia `-1 / -1` en ceros).
  - **Ubicación adaptativa más clara**: comparación de números `K1..K3` con tarjetas clicables en lugar de escritura manual y cuadrículas de columna en `S2..S5`, `R2..R5` y `M3..M5`.
  - **Mejoras por nivel sin subir dificultad**: tira posicional `antes/después/entre`, `valor_posicional` sin filtrar la respuesta en `lugar`, esquema de división con casita (`d2..d4`), fracciones con entrada apilada y MCM real (`f3`) + diagramas SVG, decimales/porcentajes con paso guiado (`p3`) y coma decimal unificada, ecuaciones de 2 pasos guiados (`a2/a3`), estadísticas con media/mediana distintas y ordenación en vivo (`da2`), y mini-tablas en Cálculo (`ca1/ca2`).
- **Nuevo actualizador visual tematizado en Lex Junior Class (`LexClassUpdateDialog`)**:
  - Reemplaza los avisos blancos del sistema por un diálogo cósmico oscuro con listado de mundos, notas, verificación SHA-256, barra de progreso en vivo (`0% → 100%`) y botón directo **`🔄 Actualizar ahora`** sin tener que salir a buscar el botón del planeta.
- **Ventana de Lex Junior Class segura para cualquier laptop y escala DPI (`125%` / `150%`)**:
  - Tamaño inicial proporcional al `86 %` del área útil (`WorkArea`), centrado estricto dentro del monitor activo (`Top >= WorkArea.Top`) para que los botones **`─ □ ✕`** y la barra superior jamás se corten o suban fuera de pantalla en laptops `768p` o con escalado alto.
- **Desbloqueo inteligente de descargas `.exe` atascadas al `100%`**:
  - Watchdog seguro en disco para finalizar descargas cuando el archivo ya existe íntegro y libre en `ResultPath`, sincronización de `IsReputationCheckingRequired` con el ajuste SmartScreen del perfil y estado claro en espera de análisis externo.
- **Estabilidad de pantalla completa (`F11` / `Esc`) y reproductor flotante (PiP)**:
  - Deshabilitado `DocumentPictureInPictureAPI` en WebView2 para evitar que «Reproductor flotante» (p. ej. TikTok) abra pestañas vacías (`about:blank`), choque con Auto-PiP o cierre el navegador al desactivar el reproductor, manteniendo intacto el PiP nativo `<video>` y **Flotar web**.
  - Mantiene adjunto `AppWindowChrome` (`CaptionHeight=0`, `ResizeBorderThickness=0`) al entrar en `F11` sin desmontar el frame nativo ni mutar `ResizeMode=NoResize`, difiere atajos desde `AcceleratorKeyPressed` fuera del callback COM síncrono, permite salir de `F11` con `Esc` y captura `BrowserProcessExited` / excepciones no observadas.

## [2.8.0] - 2026-11-22

### Novedades y mejoras
- **Lanzamiento oficial de Lex Junior Class (Prototipo en desarrollo) en ventana independiente**:
  - Nueva ventana dedicada (`LexClassWindow`) sin barra de direcciones ni historial del navegador, con modo de pantalla completa propio (`F11` / `Esc` / botón `✕` flotante superior), icono oficial propio (`lexclass.ico`), instalador propio opcional en Escritorio (`--install-lexclass`) y barra de actualizaciones modulares (`🔄 Buscar actualizaciones` → `lexclass-worlds.json` con verificación SHA-256 sin reinstalar el navegador).
  - Motor 100 % puro de **Universo de Mundos (`universo-engine`)**, eliminando por completo el portal antiguo: **Universo I · Fundamentos**, adelanto de **Universo II (+1000 años)**, **Matemáticas (5 niveles · 12 etapas · 51 misiones hasta Cálculo)**, **Inglés paso a paso + LexBook (5 niveles · 12 etapas · 24 misiones)** y **Brújula Humana**.
  - **Nuevo paisaje sonoro musical (`🎵 Ambiente`)**: música generativa 100 % offline en Web Audio API para **Matemáticas** (piano cálido / celesta estilo aventura anime calmada + brisa natural) y para **Inglés** (kalimba / arpa chill + brisa marina).
- **Depuración visual de barra superior y accesos honestos**:
  - Botones compactos (**Flotar web**, **Media**, **Lex Class**, **Telemetría?**) con estado claro de privacidad y telemetría honesta.
- **Correcciones clave de estabilidad en Lex Junior Browser**:
  - **Snap Layouts sin parpadeo**: el selector de distribución bajo el botón maximizar ya no titila al pasar el cursor por el borde superior.
  - **Descargas limpias**: corregida la advertencia falsa de cancelación al completar descargas (`DownloadManager` vs estado final de WebView2).
  - **Pantalla completa `F11`, `Ctrl+Tab` y aislamiento de YouTube**: desduplicación antirebote entre `WebView2` y WPF para evitar doble activación en 1 ms, transición sin salto `Normal → Maximized` para eliminar pantallas en blanco al cambiar de pestaña con `Ctrl+Tab`, aislamiento de fullscreen HTML5 de YouTube y botón flotante `✕` arriba al centro al acercar el mouse.

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
