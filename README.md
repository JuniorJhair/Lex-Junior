# ⚡ Lex Junior Browser (LexBrowser)

Navegador web de escritorio ligero, privado y optimizado para multimedia en Windows, desarrollado en **C# (.NET 8.0 / WPF)** sobre **Microsoft Edge WebView2 (Chromium)**.

## ✨ Características Principales
- **🛡️ LexShield AdBlock + YouTube Optimizer:** Bloqueo de red en tiempo real (403 Forbidden) contra dominios de publicidad/rastreo, neutralización de anuncios en YouTube/Shorts y cierre limpio de diálogos Polymer sin capas invisibles.
- **🔒 Modo Incógnito Real y 0 Telemetría:** Sesiones aisladas en memoria (`IsInPrivateModeEnabled = true`), sin registro en historial, sin retención en `Ctrl+Shift+T`, sin caché compartida de favicons y con protección mDNS para WebRTC.
- **🗖 Picture-in-Picture Inteligente (Auto-PiP y Página Flotante):** Mini-reproductor nativo automático al cambiar de pestaña con memoria de tamaño independiente para videos 16:9 y Shorts 9:16, más ventana flotante manual.
- **📑 Gestión Avanzada de Pestañas y Accesos Directos:** Navegación multi-pestaña con arrastrar y soltar, fijado de pestañas (solo icono o icono + título), indicador de seguridad HTTPS dinámico y barra de marcadores con persistencia atómica.
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
