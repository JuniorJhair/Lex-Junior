# ⚡ Lex Junior Browser (LexBrowser)

Navegador web de escritorio privado y optimizado para multimedia en Windows, desarrollado en **C# (.NET 8.0 / WPF)** sobre **Microsoft Edge WebView2 (Chromium)**.

## ✨ Características Principales
- **🛡️ LexShield Core (Menos anuncios) + Adaptadores Aislados:** Motor declarativo unificado en C#/.NET 8 (`lex-shield.lex`, scriptlets estáticos sin `eval`, `$csp`, `$popup` y capas anti-overlay sin doble conteo) con rutas estrictamente aisladas para YouTube y Dailymotion.
- **🔒 Perfiles de Privacidad (A / B / C / Personalizado), Modo Incógnito Real y Sin Telemetría Propia:** Sesiones privadas aisladas (`IsInPrivateModeEnabled = true`), sin registro en historial, sin retención en `Ctrl+Shift+T`, sin caché en disco de favicons y con protección mDNS para WebRTC. Incluye asistente de perfiles de privacidad (`A: Privacidad estricta`, `B: Equilibrado`, `C: Completo` y `Personalizado`) y compuerta de red de arranque (`StartupNetworkGate`).
- **📑 Lex PDF Studio (Archivos Locales y Virtuales `http/https`):** Lector y editor nativo con dibujo a mano alzada (8 grosores), resaltador, texto, firma digital en 1 clic, sellos, Modo Noche/Sepia y guardado vectorial sin pérdida (`pdf-lib`), compatible tanto con archivos locales como con enlaces PDF virtuales autenticados mediante proxy interno con protección anti-SSRF.
- **🎓 Lex Junior Class (Adelanto integrado — Próxima actualización):** Botón dedicado en la barra superior, tarjeta en Nueva Pestaña y ruta `lex://class` con el adelanto oficial de la plataforma educativa interactiva paso a paso (1.° de Primaria a Preuniversitario).
- **🗖 Picture-in-Picture Inteligente (Auto-PiP y Página Flotante):** Mini-reproductor nativo automático al cambiar de pestaña con tamaño independiente guardado para videos 16:9 y Shorts 9:16, más ventana flotante manual.
- **📑 Gestión Avanzada de Pestañas, Bóveda DPAPI + PIN y Accesos Directos:** Navegación multi-pestaña con arrastrar y soltar, Bóveda de Contraseñas cifrada con Windows DPAPI y coincidencia estricta de host, bloqueo por PIN y Modo Invitado, actualizador con verificación SHA-256 y diálogo de novedades formateado con scroll.
- **🔍 Buscador Configurable:** Omnibox y página de Nueva Pestaña sincronizados con Google, DuckDuckGo, Brave Search, Bing o buscador personalizado.

## 🏗️ Estructura del Proyecto
```text
LexBrowser/
├── Core/
│   ├── Engine/
│   │   ├── BrowserEngine.cs        # Inicialización singleton de WebView2 y flags de Chromium
│   │   └── FaviconService.cs       # Caché en memoria y resolución de favicons con aislamiento Incógnito
│   ├── AdBlock/
│   │   ├── AdBlockFilter.cs        # Filtro de red O(1) para dominios publicitarios, trackers y popunders
│   │   └── YouTubeOptimizer.cs     # Script inyectado anti-anuncios, anti-overlays y control PiP
│   ├── BookmarkManager.cs          # Persistencia atómica (.tmp) de accesos directos y función Deshacer
│   ├── HistoryManager.cs           # Fachada estática del historial de navegación
│   ├── HistoryStore.cs             # Almacén de historial con deduplicación, búsqueda y guardado diferido
│   ├── NewTabPage.cs               # Generador HTML de Nueva Pestaña normal e incógnito
│   └── SettingsManager.cs          # Persistencia atómica de preferencias y motor de búsqueda
├── UI/
│   ├── Models/
│   │   ├── BrowserTab.cs           # Modelo observable de pestaña
│   │   └── BookmarkItem.cs         # Modelo observable de acceso directo
│   └── Views/
│       ├── FloatingPipWindow.xaml  # Ventana manual de Página Flotante / Solo Video
│       ├── HistoryWindow.xaml      # Gestor de historial y limpieza aislada por origen (CDP)
│       └── SettingsWindow.xaml     # Panel de configuración del navegador
├── MainWindow.xaml                 # Interfaz principal del navegador
└── App.xaml                        # Punto de entrada de la aplicación
```

## 🚀 Requisitos y Ejecución
1. Tener instalado **.NET 8.0 SDK** y **Microsoft Edge WebView2 Runtime**.
2. Ejecutar desde PowerShell en la raíz del proyecto:
   ```powershell
   .\run.ps1
   ```
   O manualmente:
   ```powershell
   dotnet build
   dotnet run --project LexBrowser
   ```

## ⌨️ Atajos de Teclado
| Atajo | Acción |
|---|---|
| `Ctrl + T` / `Ctrl + N` | Abrir nueva pestaña normal |
| `Ctrl + Shift + N` | Abrir nueva pestaña en Modo Incógnito |
| `Ctrl + W` | Cerrar pestaña actual |
| `Ctrl + Shift + T` | Reabrir última pestaña normal cerrada |
| `Ctrl + Tab` / `Ctrl + Shift + Tab` | Cambiar a pestaña siguiente / anterior |
| `Ctrl + 1..8` / `Ctrl + 9` | Saltar a pestaña por número / última pestaña |
| `Ctrl + R` / `F5` | Recargar página |
| `Ctrl + L` / `Alt + D` | Seleccionar barra de direcciones (Omnibox) |
| `Ctrl + H` | Abrir Historial y Limpieza de Datos |
| `Ctrl + ,` | Abrir Configuración |
| `Ctrl + Shift + B` | Mostrar / Ocultar barra de accesos directos |
| `Ctrl + +` / `Ctrl + -` / `Ctrl + 0` | Aumentar, reducir o restablecer zoom al 100% |
