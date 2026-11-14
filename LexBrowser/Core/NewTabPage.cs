using System;
using System.IO;

namespace LexBrowser.Core;

public static class NewTabPage
{
    private static string? _logoBase64;

    private static string ResizeAndEncodeBase64(byte[] rawBytes, int maxWidth = 128, int maxHeight = 128)
    {
        try
        {
            using var ms = new MemoryStream(rawBytes);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(ms, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            double scale = Math.Min((double)maxWidth / frame.PixelWidth, (double)maxHeight / frame.PixelHeight);
            if (scale >= 1.0) scale = 1.0;
            var transformed = new System.Windows.Media.Imaging.TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale));
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(transformed));
            using var outMs = new MemoryStream();
            encoder.Save(outMs);
            return Convert.ToBase64String(outMs.ToArray());
        }
        catch
        {
            if (rawBytes.Length <= 65536)
                return Convert.ToBase64String(rawBytes);
            return "";
        }
    }

    private static string GetLogoBase64()
    {
        if (_logoBase64 != null) return _logoBase64;

        // 1. Intentar cargar primero la versión web optimizada (~23 KB) desde los recursos ensamblados
        try
        {
            var webUri = new Uri("pack://application:,,,/Assets/logo_web.png", UriKind.Absolute);
            var streamInfo = System.Windows.Application.GetResourceStream(webUri);
            if (streamInfo?.Stream != null)
            {
                using var ms = new MemoryStream();
                streamInfo.Stream.CopyTo(ms);
                _logoBase64 = Convert.ToBase64String(ms.ToArray());
                return _logoBase64;
            }
        }
        catch { }

        // 2. Intentar archivo físico de logo_web.png
        try
        {
            string webPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "logo_web.png");
            if (File.Exists(webPath))
            {
                _logoBase64 = Convert.ToBase64String(File.ReadAllBytes(webPath));
                return _logoBase64;
            }
            string devWebPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Assets", "logo_web.png");
            if (File.Exists(devWebPath))
            {
                _logoBase64 = Convert.ToBase64String(File.ReadAllBytes(devWebPath));
                return _logoBase64;
            }
        }
        catch { }

        // 3. Fallback a logo.png pero reescalado en memoria a 128x128 para jamás exceder límites de NavigateToString
        try
        {
            var resourceUri = new Uri("pack://application:,,,/Assets/logo.png", UriKind.Absolute);
            var streamInfo = System.Windows.Application.GetResourceStream(resourceUri);
            if (streamInfo?.Stream != null)
            {
                using var ms = new MemoryStream();
                streamInfo.Stream.CopyTo(ms);
                _logoBase64 = ResizeAndEncodeBase64(ms.ToArray());
                return _logoBase64;
            }
        }
        catch { }

        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "logo.png");
            if (File.Exists(path))
            {
                _logoBase64 = ResizeAndEncodeBase64(File.ReadAllBytes(path));
                return _logoBase64;
            }
        }
        catch { }

        return "";
    }

    public static string GetHtml(bool incognito = false, bool isGuest = false)
    {
        string theme = BrowserThemeManager.NormalizeTheme(SettingsManager.Current?.Theme);
        bool isLight = theme == BrowserThemeManager.Light && !incognito && !isGuest;
        bool isClassic = theme == BrowserThemeManager.Classic && !incognito && !isGuest;

        string bg = isGuest ? "#070D18" : (incognito ? "#0A0010" : (isLight ? "#F8FAFC" : (isClassic ? "#000000" : "#0F0F12")));
        string glow = isGuest ? "rgba(56,189,248,0.12)" : (incognito ? "rgba(139,92,246,0.12)" : (isClassic ? "rgba(255,107,0,0.10)" : (isLight ? "rgba(37,99,235,0.06)" : "rgba(99,102,241,0.09)")));
        string badgeDisplay = (incognito || isGuest) ? "flex" : "none";
        string accent = isGuest ? "#38BDF8" : (incognito ? "#8B5CF6" : (isClassic ? "#FF6B00" : (isLight ? "#2563EB" : "#6366F1")));
        string accentBtn = isGuest ? "#0284C7" : (incognito ? "#7C3AED" : (isClassic ? "#E05E00" : (isLight ? "#2563EB" : "#6366F1")));
        string focusGlow = isGuest ? "rgba(56,189,248,0.18)" : (incognito ? "rgba(139,92,246,0.14)" : (isClassic ? "rgba(255,107,0,0.20)" : (isLight ? "rgba(37,99,235,0.18)" : "rgba(99,102,241,0.14)")));
        string hoverBorder = isGuest ? "rgba(56,189,248,0.45)" : (incognito ? "rgba(139,92,246,0.4)" : (isClassic ? "rgba(255,107,0,0.5)" : (isLight ? "rgba(37,99,235,0.4)" : "rgba(99,102,241,0.35)")));

        string badgeText = isGuest
            ? "👤 Modo Invitado — Sesión limpia y aislada"
            : "🕵️ Modo incógnito activo — Privacidad total";
        string badgeBg = isGuest ? "rgba(56,189,248,0.12)" : "rgba(139,92,246,0.12)";
        string badgeBorder = isGuest ? "rgba(56,189,248,0.30)" : "rgba(139,92,246,0.25)";
        string badgeColor = isGuest ? "#38BDF8" : "#A78BFA";

        string brandTitleGradient = isGuest
            ? "linear-gradient(135deg, #38BDF8 0%, #60A5FA 100%)"
            : (incognito
                ? "linear-gradient(135deg, #A855F7 0%, #C084FC 100%)"
                : (isClassic
                    ? "linear-gradient(135deg, #FF6B00 0%, #FFA500 100%)"
                    : (isLight
                        ? "linear-gradient(135deg, #2563EB 0%, #4F46E5 100%)"
                        : "linear-gradient(135deg, #60A5FA 0%, #A855F7 100%)")));

        string bodyText = isLight ? "#0F172A" : "#E4E4E7";
        string clockColor = isLight ? "#0F172A" : "#F4F4F5";
        string dateColor = isLight ? "#64748B" : "#52525B";
        string searchBarBg = isLight ? "#FFFFFF" : (isClassic ? "#111111" : "#18181D");
        string searchBarBorder = isLight ? "#CBD5E1" : (isClassic ? "#262626" : "#272730");
        string searchBarShadow = isLight ? "0 4px 20px rgba(15,23,42,0.06)" : "none";
        string searchTextColor = isLight ? "#0F172A" : "#F4F4F5";
        string placeholderColor = isLight ? "#94A3B8" : "#52525B";
        string quickLinkBg = isLight ? "#FFFFFF" : (isClassic ? "#0F0F0F" : "#18181D");
        string quickLinkBorder = isLight ? "#E2E8F0" : (isClassic ? "#222222" : "#1E1E28");
        string quickLinkText = isLight ? "#334155" : "#71717A";
        string quickLinkHoverBg = isLight ? "#F1F5F9" : (isClassic ? "#1A1A1A" : "#22222E");
        string quickLinkHoverText = isLight ? "#0F172A" : "#D4D4D8";
        string footerColor = isLight ? "#64748B" : "#3F3F46";

        string logoBase64 = GetLogoBase64();
        string logoHtml = !string.IsNullOrEmpty(logoBase64)
            ? @"<img src=""data:image/png;base64," + logoBase64 + @""" class=""logo-img"" alt=""Lex Junior Logo""/>"
            : @"<div class=""logo-fallback"">LJ</div>";

        string engineName = (SettingsManager.Current?.DefaultSearchEngine ?? "Google") switch
        {
            "DuckDuckGo" => "DuckDuckGo",
            "Brave" => "Brave Search",
            "Bing" => "Bing",
            "Custom" => " tu buscador",
            _ => "Google"
        };
        string searchUrlTemplate = SettingsManager.GetSearchUrl("__LEX_QUERY__").Replace("'", "\\'");

        return @"<!DOCTYPE html>
<html lang=""es"">
<head>
  <meta charset=""UTF-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
  <title>Nueva pestaña</title>
  <style>
    * { margin: 0; padding: 0; box-sizing: border-box; }

    body {
      background: " + bg + @";
      color: " + bodyText + @";
      font-family: 'Segoe UI', -apple-system, BlinkMacSystemFont, sans-serif;
      min-height: 100vh;
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: space-between;
      padding: clamp(14px, 3vh, 36px) 16px 14px;
      overflow-x: hidden;
      overflow-y: auto;
      user-select: none;
    }

    .bg-glow {
      position: fixed; top: -220px; left: 50%;
      transform: translateX(-50%);
      width: 900px; height: 600px;
      background: radial-gradient(ellipse, " + glow + @" 0%, transparent 70%);
      pointer-events: none; z-index: 0;
    }

    .content {
      position: relative; z-index: 1;
      display: flex; flex-direction: column; align-items: center;
      margin: auto 0;
      width: 100%;
    }

    .brand-wrap {
      display: flex; align-items: center; gap: 14px;
      margin-bottom: clamp(10px, 2.2vh, 22px);
    }

    .logo-img {
      width: clamp(52px, 8vh, 72px); height: clamp(52px, 8vh, 72px); object-fit: contain;
      filter: drop-shadow(0 0 18px rgba(99,102,241,0.4));
      border-radius: 16px;
    }

    .logo-fallback {
      width: 58px; height: 58px; border-radius: 14px;
      background: linear-gradient(135deg, #00D2FF 0%, #7C3AED 100%);
      display: flex; align-items: center; justify-content: center;
      font-weight: 900; font-size: 24px; color: #FFFFFF;
      box-shadow: 0 0 20px rgba(99,102,241,0.4);
    }

    .brand-title {
      font-size: clamp(20px, 3.2vh, 26px); font-weight: 800; letter-spacing: 1px;
      background: " + brandTitleGradient + @";
      -webkit-background-clip: text; -webkit-text-fill-color: transparent;
    }

    .incognito-badge {
      display: " + badgeDisplay + @";
      align-items: center; gap: 8px;
      background: " + badgeBg + @";
      border: 1px solid " + badgeBorder + @";
      border-radius: 20px; padding: 6px 16px;
      color: " + badgeColor + @"; font-size: 12px; font-weight: 600;
      margin-bottom: clamp(10px, 2vh, 20px); letter-spacing: 0.5px;
    }

    .clock {
      font-size: clamp(44px, 8.5vh, 68px); font-weight: 200;
      letter-spacing: -2px; color: " + clockColor + @";
      margin-bottom: 4px; line-height: 1;
      font-variant-numeric: tabular-nums;
    }

    .date {
      font-size: 13px; color: " + dateColor + @";
      margin-bottom: clamp(14px, 3.2vh, 34px); letter-spacing: 0.5px;
      text-transform: capitalize; font-weight: 400;
    }

    .search-wrap { width: min(680px, 94vw); margin-bottom: clamp(16px, 3.4vh, 36px); }

    .search-bar {
      display: flex; align-items: center; gap: 6px;
      background: " + searchBarBg + @";
      border: 1.5px solid " + searchBarBorder + @";
      box-shadow: " + searchBarShadow + @";
      border-radius: 32px; padding: 7px 14px;
      transition: border-color 0.2s, box-shadow 0.2s;
    }

    .search-bar:focus-within {
      border-color: " + accent + @";
      box-shadow: 0 0 0 4px " + focusGlow + @";
    }

    .icon-btn {
      background: transparent;
      border: none;
      cursor: pointer;
      display: flex;
      align-items: center;
      justify-content: center;
      width: 32px; height: 32px;
      border-radius: 50%;
      color: #9AA0A6;
      transition: background 0.15s, transform 0.15s;
      flex-shrink: 0;
      padding: 0;
    }
    .icon-btn:hover {
      background: rgba(255,255,255,0.08);
      transform: scale(1.08);
    }
    .icon-btn:active {
      transform: scale(0.95);
    }

    @keyframes micPulse {
      0% { transform: scale(1); filter: drop-shadow(0 0 0 rgba(234, 67, 53, 0.7)); }
      50% { transform: scale(1.15); filter: drop-shadow(0 0 10px rgba(234, 67, 53, 0.9)); }
      100% { transform: scale(1); filter: drop-shadow(0 0 0 rgba(234, 67, 53, 0.7)); }
    }
    .mic-listening {
      animation: micPulse 1.2s infinite ease-in-out;
      background: rgba(234, 67, 53, 0.2) !important;
      color: #EA4335 !important;
    }

    .voice-msg {
      margin-top: 12px;
      font-size: 13px;
      color: #F87171;
      background: rgba(239, 68, 68, 0.12);
      border: 1px solid rgba(239, 68, 68, 0.3);
      border-radius: 8px;
      padding: 8px 14px;
      max-width: 600px;
      text-align: center;
      line-height: 1.4;
    }

    .ai-mode-btn {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      background: #23232A;
      border: 1px solid #32323E;
      color: #E4E4E7;
      font-size: 12.5px;
      font-weight: 600;
      padding: 6px 12px;
      border-radius: 20px;
      cursor: pointer;
      transition: all 0.2s ease;
      white-space: nowrap;
      flex-shrink: 0;
    }
    .ai-mode-btn:hover {
      background: #2C2C36;
      border-color: #6366F1;
      color: #FFFFFF;
      box-shadow: 0 0 14px rgba(99,102,241,0.25);
      transform: translateY(-1px);
    }

    .search-bar input {
      flex: 1; min-width: 0; background: transparent; border: none; outline: none;
      color: " + searchTextColor + @"; font-size: 15px;
      caret-color: " + accent + @";
      padding: 4px 6px;
    }

    .search-bar input::placeholder { color: " + placeholderColor + @"; }

    .search-btn {
      background: " + accentBtn + @";
      border: none; border-radius: 20px;
      color: white; padding: 7px 16px;
      cursor: pointer; font-size: 13px; font-weight: 500;
      transition: opacity 0.15s; flex-shrink: 0;
    }
    .search-btn:hover { opacity: 0.88; }

    .quick-links {
      display: flex; gap: 12px; flex-wrap: wrap;
      justify-content: center; max-width: min(680px, 94vw);
    }

    .quick-link {
      display: flex; flex-direction: column; align-items: center; gap: 8px;
      padding: 13px 12px 10px;
      background: " + quickLinkBg + @";
      border: 1px solid " + quickLinkBorder + @";
      border-radius: 14px; cursor: pointer;
      width: 82px; text-decoration: none;
      color: " + quickLinkText + @"; font-size: 11px;
      transition: all 0.18s ease;
    }

    .quick-link:hover {
      background: " + quickLinkHoverBg + @";
      border-color: " + hoverBorder + @";
      color: " + quickLinkHoverText + @";
      transform: translateY(-3px);
      box-shadow: 0 8px 24px rgba(0,0,0,0.35);
    }

    .quick-link .ico { width: 24px; height: 24px; display: flex; align-items: center; justify-content: center; }
    .quick-link .ico svg { width: 22px; height: 22px; display: block; }

    .footer {
      position: relative; z-index: 1;
      margin-top: 16px;
      text-align: center;
      color: #3F3F46; font-size: 11px; letter-spacing: 0.3px;
    }
    .footer b { color: " + accent + @"; font-weight: 600; }
  </style>
</head>
<body>
  <div class=""bg-glow""></div>
  <div class=""content"">
    <div class=""brand-wrap"">
      " + logoHtml + @"
      <div class=""brand-title"">LEX JUNIOR</div>
    </div>

    <div class=""incognito-badge"">🕵️ Modo incógnito activo — Privacidad total</div>

    <div class=""clock"" id=""clock"">00:00</div>
    <div class=""date"" id=""date""></div>

    <div class=""search-wrap"">
      <div class=""search-bar"">
        <input type=""file"" id=""file-input"" accept=""image/*"" style=""display:none"" onchange=""handleFileSelected(event)""/>
        <button class=""icon-btn"" title=""Subir archivo o imagen"" onclick=""document.getElementById('file-input').click()"">
          <svg viewBox=""0 0 24 24"" width=""18"" height=""18"" fill=""none"" stroke=""#9AA0A6"" stroke-width=""2.2"" stroke-linecap=""round"">
            <line x1=""12"" y1=""5"" x2=""12"" y2=""19""></line>
            <line x1=""5"" y1=""12"" x2=""19"" y2=""12""></line>
          </svg>
        </button>
        <input type=""text"" placeholder=""Buscar en " + engineName + @" o escribir una URL..."" id=""si"" autocomplete=""off"" spellcheck=""false""/>
        <button class=""icon-btn"" id=""mic-btn"" title=""Buscar por voz"" onclick=""startVoiceSearch()"">
          <svg viewBox=""0 0 24 24"" width=""19"" height=""19"">
            <path fill=""#4285F4"" d=""M12 14a3 3 0 0 0 3-3V5a3 3 0 0 0-6 0v6a3 3 0 0 0 3 3z""/>
            <path fill=""#34A853"" d=""M11 18.92V22h2v-3.08c3.39-.49 6-3.39 6-6.92h-2a5 5 0 0 1-10 0H5c0 3.53 2.61 6.43 6 6.92z""/>
            <path fill=""#FBBC05"" d=""M7 11H5a7 7 0 0 0 7 7v-2a5 5 0 0 1-5-5z""/>
            <path fill=""#EA4335"" d=""M12 16a5 5 0 0 0 5-5h2a7 7 0 0 1-7 7v-2z""/>
          </svg>
        </button>
        <button class=""icon-btn"" title=""Búsqueda por imágenes (Google Lens)"" onclick=""openLens()"">
          <svg viewBox=""0 0 24 24"" width=""19"" height=""19"">
            <path fill=""#4285F4"" d=""M5 15v4a2 2 0 0 0 2 2h4v-2H7v-4H5z""/>
            <path fill=""#34A853"" d=""M19 15v4a2 2 0 0 1-2 2h-4v-2h4v-4h2z""/>
            <path fill=""#FBBC05"" d=""M5 9V5a2 2 0 0 1 2-2h4v2H7v4H5z""/>
            <path fill=""#EA4335"" d=""M19 9V5a2 2 0 0 0-2-2h-4v2h4v4h2z""/>
            <circle cx=""12"" cy=""12"" r=""3.2"" fill=""#4285F4""/>
            <circle cx=""15.5"" cy=""8.5"" r=""1.2"" fill=""#34A853""/>
          </svg>
        </button>
        <button class=""ai-mode-btn"" title=""Modo IA (Búsqueda inteligente / Gemini)"" onclick=""goAI()"">
          <svg viewBox=""0 0 24 24"" width=""15"" height=""15"" fill=""none"">
            <path d=""M12 2L14.39 9.61L22 12L14.39 14.39L12 22L9.61 14.39L2 12L9.61 9.61L12 2Z"" fill=""url(#ai-grad)""/>
            <defs>
              <linearGradient id=""ai-grad"" x1=""2"" y1=""2"" x2=""22"" y2=""22"" gradientUnits=""userSpaceOnUse"">
                <stop stop-color=""#60A5FA""/>
                <stop offset=""0.5"" stop-color=""#A855F7""/>
                <stop offset=""1"" stop-color=""#EC4899""/>
              </linearGradient>
            </defs>
          </svg>
          <span>Modo IA</span>
        </button>
        <button class=""search-btn"" onclick=""go()"">Buscar</button>
      </div>
      <div id=""voice-msg"" class=""voice-msg"" style=""display:none;""></div>
    </div>

    <div class=""quick-links"">
      <a class=""quick-link"" href=""https://www.youtube.com"">
        <div class=""ico"">
          <svg viewBox=""0 0 24 24"" xmlns=""http://www.w3.org/2000/svg"">
            <path d=""M23.498 6.186a3.016 3.016 0 0 0-2.122-2.136C19.505 3.545 12 3.545 12 3.545s-7.505 0-9.377.505A3.017 3.017 0 0 0 .502 6.186C0 8.07 0 12 0 12s0 3.93.502 5.814a3.016 3.016 0 0 0 2.122 2.136c1.871.505 9.376.505 9.376.505s7.505 0 9.377-.505a3.015 3.015 0 0 0 2.122-2.136C24 15.93 24 12 24 12s0-3.93-.502-5.814z"" fill=""#FF0000""/>
            <path d=""M9.545 15.568V8.432L15.818 12l-6.273 3.568z"" fill=""#FFFFFF""/>
          </svg>
        </div>YouTube
      </a>
      <a class=""quick-link"" href=""https://www.google.com"">
        <div class=""ico"">
          <svg viewBox=""0 0 24 24"" xmlns=""http://www.w3.org/2000/svg"">
            <path d=""M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92c-.26 1.37-1.04 2.53-2.21 3.31v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.09z"" fill=""#4285F4""/>
            <path d=""M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z"" fill=""#34A853""/>
            <path d=""M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.07H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.93l3.66-2.84z"" fill=""#FBBC05""/>
            <path d=""M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.07l3.66 2.84c.87-2.6 3.3-4.53 6.16-4.53z"" fill=""#EA4335""/>
          </svg>
        </div>Google
      </a>
      <a class=""quick-link"" href=""https://mail.google.com"">
        <div class=""ico"">
          <svg viewBox=""0 0 24 24"" xmlns=""http://www.w3.org/2000/svg"">
            <path d=""M2 6.5V18c0 1.1.9 2 2 2h2V9.8l6 4.5 6-4.5V20h2c1.1 0 2-.9 2-2V6.5c0-1.8-2.1-2.8-3.5-1.7L12 10 5.5 4.8C4.1 3.7 2 4.7 2 6.5z"" fill=""#EA4335""/>
            <path d=""M2 6.5V18c0 1.1.9 2 2 2h2V9.8L2 6.5z"" fill=""#4285F4""/>
            <path d=""M22 6.5V18c0 1.1-.9 2-2 2h-2V9.8l4-3.3z"" fill=""#34A853""/>
            <path d=""M18 9.8V4.5l2.5 1.8c.9.7 1.5 1.1 1.5 2.2L18 9.8z"" fill=""#FBBC04""/>
            <path d=""M6 9.8V4.5L3.5 6.3C2.6 7 2 7.4 2 8.5L6 9.8z"" fill=""#C5221F""/>
          </svg>
        </div>Gmail
      </a>
      <a class=""quick-link"" href=""https://calendar.google.com"">
        <div class=""ico"">
          <svg viewBox=""0 0 24 24"" xmlns=""http://www.w3.org/2000/svg"">
            <rect x=""3"" y=""3"" width=""18"" height=""18"" rx=""3"" fill=""#FFFFFF""/>
            <path d=""M3 6a3 3 0 0 1 3-3h12a3 3 0 0 1 3 3v2H3V6z"" fill=""#4285F4""/>
            <path d=""M18 3h3v5h-3V3z"" fill=""#1967D2""/>
            <path d=""M3 18h18v0a3 3 0 0 1-3 3H6a3 3 0 0 1-3-3z"" fill=""#34A853""/>
            <path d=""M18 16h3v2a3 3 0 0 1-3 3v-5z"" fill=""#EA4335""/>
            <path d=""M3 8h3v10H3V8z"" fill=""#FBBC04""/>
            <text x=""12"" y=""16.2"" text-anchor=""middle"" font-family=""Segoe UI,Arial,sans-serif"" font-weight=""800"" font-size=""8.5"" fill=""#1A73E8"">31</text>
          </svg>
        </div>Calendar
      </a>
      <a class=""quick-link"" href=""https://drive.google.com"">
        <div class=""ico"">
          <svg viewBox=""0 0 24 24"" xmlns=""http://www.w3.org/2000/svg"">
            <path d=""M8.5 3.5L15.5 3.5L22 14.8L15 14.8Z"" fill=""#FBBC04""/>
            <path d=""M8.5 3.5L12 9.5L5.5 20.5L2 14.8Z"" fill=""#0F9D58""/>
            <path d=""M5.5 20.5L18.5 20.5L22 14.8L9 14.8Z"" fill=""#4285F4""/>
          </svg>
        </div>Drive
      </a>
      <a class=""quick-link"" href=""https://www.twitch.tv"">
        <div class=""ico"">
          <svg viewBox=""0 0 24 24"" xmlns=""http://www.w3.org/2000/svg"">
            <path d=""M4.3 3H21v11.7l-4.7 4.7h-3.9l-2.5 2.4H7v-2.4H3V6.2L4.3 3z"" fill=""#9146FF""/>
            <path d=""M19 13.8V5H6v12h3v2.4l2.4-2.4h4.5L19 13.8z"" fill=""#FFFFFF""/>
            <path d=""M15 8h2v4.7h-2V8zm-4 0h2v4.7h-2V8z"" fill=""#9146FF""/>
          </svg>
        </div>Twitch
      </a>
      <a class=""quick-link"" href=""https://twitter.com"">
        <div class=""ico"">
          <svg viewBox=""0 0 24 24"" xmlns=""http://www.w3.org/2000/svg"">
            <path d=""M18.244 2.25h3.308l-7.227 8.26 8.502 11.24H16.17l-5.214-6.817L4.99 21.75H1.68l7.73-8.835L1.254 2.25H8.08l4.713 6.231zm-1.161 17.52h1.833L7.084 4.126H5.117z"" fill=""#F4F4F5""/>
          </svg>
        </div>X
      </a>
      <a class=""quick-link"" href=""https://tv.lex.local/index.html"">
        <div class=""ico"">
          <svg viewBox=""0 0 24 24"" xmlns=""http://www.w3.org/2000/svg"">
            <rect x=""2"" y=""6"" width=""20"" height=""14"" rx=""3"" fill=""#6366F1""/>
            <path d=""M8 3l4 3 4-3"" stroke=""#38BDF8"" stroke-width=""2"" fill=""none"" stroke-linecap=""round""/>
            <polygon points=""10,10 16,13 10,16"" fill=""#FFFFFF""/>
          </svg>
        </div>TV en vivo
      </a>
    </div>
  </div>

  <div class=""footer"">⚡ <b>Lex Junior Browser</b> — Cero telemetría · Sin anuncios · Ultra-ligero</div>

  <script>
    function tick() {
      const n = new Date();
      const h = String(n.getHours()).padStart(2,'0');
      const m = String(n.getMinutes()).padStart(2,'0');
      document.getElementById('clock').textContent = h + ':' + m;
      const opts = { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' };
      document.getElementById('date').textContent = n.toLocaleDateString('es-ES', opts);
    }
    tick();
    setInterval(() => { if (!document.hidden) tick(); }, 10000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) tick(); });

    function go() {
      const v = document.getElementById('si').value.trim();
      if (!v) return;
      if (/^lex:(?:\/\/)?tv$/i.test(v)) {
        window.location.href = 'https://tv.lex.local/index.html';
        return;
      }
      if (/^(https?|file|about|view-source|data):/i.test(v)) {
        window.location.href = v;
      } else if (/^[a-zA-Z]:[\\\/]/i.test(v)) {
        window.location.href = 'file:///' + v.replace(/\\/g, '/');
      } else if (/^[a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?(\.[a-zA-Z]{2,})+/i.test(v) && !v.includes(' ')) {
        window.location.href = 'https://' + v;
      } else {
        const tpl = '" + searchUrlTemplate + @"';
        window.location.href = tpl.replace('__LEX_QUERY__', encodeURIComponent(v));
      }
    }

    document.getElementById('si').addEventListener('keydown', function(e) {
      if (e.key === 'Enter') go();
    });

    function handleFileSelected(e) {
      if (e.target.files && e.target.files.length > 0) {
        window.location.href = 'https://lens.google.com/';
      }
    }

    function openLens() {
      window.location.href = 'https://lens.google.com/';
    }

    function goAI() {
      const v = document.getElementById('si').value.trim();
      if (v) {
        window.location.href = 'https://www.google.com/search?q=' + encodeURIComponent(v) + '&udm=ai';
      } else {
        window.location.href = 'https://gemini.google.com/';
      }
    }

    function showVoiceMessage(msg) {
      const el = document.getElementById('voice-msg');
      if (!el) return;
      el.textContent = msg;
      el.style.display = 'block';
    }

    function hideVoiceMessage() {
      const el = document.getElementById('voice-msg');
      if (el) el.style.display = 'none';
    }

    function startVoiceSearch() {
      hideVoiceMessage();
      const SpeechRec = window.SpeechRecognition || window.webkitSpeechRecognition;
      const mic = document.getElementById('mic-btn');
      if (!SpeechRec) {
        showVoiceMessage('La búsqueda por voz no está disponible en este motor');
        if (mic) mic.style.display = 'none';
        return;
      }
      try {
        const rec = new SpeechRec();
        rec.lang = 'es-ES';
        rec.interimResults = false;
        if (mic) mic.classList.add('mic-listening');
        rec.onresult = function(ev) {
          if (ev.results && ev.results[0] && ev.results[0][0]) {
            const txt = ev.results[0][0].transcript;
            document.getElementById('si').value = txt;
            if (mic) mic.classList.remove('mic-listening');
            go();
          }
        };
        rec.onerror = function(ev) {
          if (mic) mic.classList.remove('mic-listening');
          const err = ev && ev.error ? ev.error : '';
          if (err === 'not-allowed') {
            showVoiceMessage('El acceso al micrófono está bloqueado. Si usas Windows, verifica que el acceso al micrófono para aplicaciones de escritorio esté activado en Configuración > Privacidad y seguridad > Micrófono.');
          } else if (err === 'network' || err === 'service-not-allowed') {
            showVoiceMessage('La búsqueda por voz no está disponible en este motor');
            if (mic) mic.style.display = 'none';
          }
        };
        rec.onend = function() {
          if (mic) mic.classList.remove('mic-listening');
        };
        rec.start();
      } catch (err) {
        if (mic) mic.classList.remove('mic-listening');
      }
    }
  </script>
</body>
</html>";
    }
}
