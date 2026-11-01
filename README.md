# ⚡ Lex Browser

> **El navegador de escritorio ultra-ligero, sin telemetría, con bloqueo total de publicidad en YouTube y mini-reproductor flotante estilo Opera.**

---

## 🚀 Características Principales

1. **🛡️ Bloqueo de Anuncios Doble Capa (Especializado en YouTube):**
   - **Capa de Red (C#):** Intercepta solicitudes con `WebResourceRequested` y bloquea dominios publicitarios, de rastreo y telemetría antes de que toquen la red.
   - **Capa de Scriptlets (YouTube):** Neutraliza anuncios de video al instante, silencia y salta slots publicitarios a 16x de velocidad, y elimina automáticamente el banner de detección anti-adblock de YouTube.

2. **🗖 Mini-Reproductor Flotante estilo Opera (PiP Automático):**
   - Al cambiar de pestaña mientras un video se está reproduciendo, el video **te acompaña automáticamente** a una ventana flotante (*TopMost*).
   - Controles interactivos sincronizados: Pausar/Reanudar, retroceder/avanzar 5s, silenciar, barra de tiempo y fondo con la miniatura del video.
   - Botón **"⇱ Volver"**: Te devuelve al instante a la pestaña de origen en la ventana principal.
   - Al volver a la pestaña de YouTube, el mini-reproductor se oculta automáticamente para verlo en grande.

3. **⚡ Motor Chromium "Desparasitado" (Cero Telemetría):**
   - Flags configurados a nivel de motor:
     `--disable-background-networking`, `--disable-sync`, `--no-pings`, `--disable-breakpad`, `--disable-domain-reliability`, `--disable-client-side-phishing-detection`, `--disable-features=OptimizationHints,Translate,InterestCohort`.
   - Cero procesos ocultos comiendo RAM o enviando datos a servidores externos.

4. **🎨 Interfaz Minimalista y Moderna (Dark Theme):**
   - Tema oscuro de alta fidelidad (#0F0F12 / Obsidian).
   - Pestañas con indicador de audio en tiempo real (`🔊`).
   - Omnibox inteligente con búsqueda privada (DuckDuckGo) y contador de anuncios bloqueados en tiempo real (`🛡️`).

---

## 🛠️ Cómo Ejecutar

Desde la terminal en esta carpeta:
```powershell
.\run.ps1
```

O compilando directamente con .NET:
```powershell
dotnet run --project LexBrowser
```
