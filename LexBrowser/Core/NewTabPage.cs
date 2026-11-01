using System;
using System.IO;

namespace LexBrowser.Core;

public static class NewTabPage
{
    private static string? _logoBase64;

    private static string GetLogoBase64()
    {
        if (_logoBase64 != null) return _logoBase64;
        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "logo.png");
            if (File.Exists(path))
            {
                _logoBase64 = Convert.ToBase64String(File.ReadAllBytes(path));
                return _logoBase64;
            }
            string devPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Assets", "logo.png");
            if (File.Exists(devPath))
            {
                _logoBase64 = Convert.ToBase64String(File.ReadAllBytes(devPath));
                return _logoBase64;
            }
        }
        catch { }
        return "";
    }

    public static string GetHtml(bool incognito = false)
    {
        string bg = incognito ? "#0A0010" : "#0F0F12";
        string glow = incognito ? "rgba(139,92,246,0.12)" : "rgba(99,102,241,0.09)";
        string badgeDisplay = incognito ? "flex" : "none";
        string accent = incognito ? "#8B5CF6" : "#6366F1";
        string accentBtn = incognito ? "#7C3AED" : "#6366F1";
        string focusGlow = incognito ? "rgba(139,92,246,0.14)" : "rgba(99,102,241,0.14)";
        string hoverBorder = incognito ? "rgba(139,92,246,0.4)" : "rgba(99,102,241,0.35)";

        string logoBase64 = GetLogoBase64();
        string logoHtml = !string.IsNullOrEmpty(logoBase64)
            ? @"<img src=""data:image/png;base64," + logoBase64 + @""" class=""logo-img"" alt=""Lex Junior Logo""/>"
            : @"<div class=""logo-fallback"">LJ</div>";

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
      color: #E4E4E7;
      font-family: 'Segoe UI', -apple-system, BlinkMacSystemFont, sans-serif;
      height: 100vh;
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      overflow: hidden;
      user-select: none;
    }

    .bg-glow {
      position: fixed; top: -220px; left: 50%;
      transform: translateX(-50%);
      width: 900px; height: 600px;
      background: radial-gradient(ellipse, " + glow + @" 0%, transparent 70%);
      pointer-events: none; z-index: 0;
    }

    .content { position: relative; z-index: 1; display: flex; flex-direction: column; align-items: center; }

    .brand-wrap {
      display: flex; align-items: center; gap: 14px;
      margin-bottom: 22px;
    }

    .logo-img {
      width: 72px; height: 72px; object-fit: contain;
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
      font-size: 26px; font-weight: 800; letter-spacing: 1px;
      background: linear-gradient(135deg, #60A5FA 0%, #A855F7 100%);
      -webkit-background-clip: text; -webkit-text-fill-color: transparent;
    }

    .incognito-badge {
      display: " + badgeDisplay + @";
      align-items: center; gap: 8px;
      background: rgba(139,92,246,0.12);
      border: 1px solid rgba(139,92,246,0.25);
      border-radius: 20px; padding: 6px 16px;
      color: #A78BFA; font-size: 12px; font-weight: 600;
      margin-bottom: 20px; letter-spacing: 0.5px;
    }

    .clock {
      font-size: 68px; font-weight: 200;
      letter-spacing: -2px; color: #F4F4F5;
      margin-bottom: 4px; line-height: 1;
      font-variant-numeric: tabular-nums;
    }

    .date {
      font-size: 13px; color: #52525B;
      margin-bottom: 34px; letter-spacing: 0.5px;
      text-transform: capitalize; font-weight: 400;
    }

    .search-wrap { width: 560px; margin-bottom: 36px; }

    .search-bar {
      display: flex; align-items: center;
      background: #18181D;
      border: 1.5px solid #272730;
      border-radius: 32px; padding: 12px 20px;
      transition: border-color 0.2s, box-shadow 0.2s;
    }

    .search-bar:focus-within {
      border-color: " + accent + @";
      box-shadow: 0 0 0 4px " + focusGlow + @";
    }

    .g-logo { width: 22px; height: 22px; margin-right: 12px; flex-shrink: 0; }

    .search-bar input {
      flex: 1; background: transparent; border: none; outline: none;
      color: #F4F4F5; font-size: 15px;
      caret-color: " + accent + @";
    }

    .search-bar input::placeholder { color: #52525B; }

    .search-btn {
      background: " + accentBtn + @";
      border: none; border-radius: 22px;
      color: white; padding: 8px 18px;
      cursor: pointer; font-size: 13px; font-weight: 500;
      transition: opacity 0.15s; flex-shrink: 0; margin-left: 10px;
    }
    .search-btn:hover { opacity: 0.88; }

    .quick-links {
      display: flex; gap: 12px; flex-wrap: wrap;
      justify-content: center; max-width: 620px;
    }

    .quick-link {
      display: flex; flex-direction: column; align-items: center; gap: 8px;
      padding: 14px 14px 11px;
      background: #18181D;
      border: 1px solid #1E1E28;
      border-radius: 14px; cursor: pointer;
      width: 82px; text-decoration: none;
      color: #71717A; font-size: 11px;
      transition: all 0.18s ease;
    }

    .quick-link:hover {
      background: #22222E;
      border-color: " + hoverBorder + @";
      color: #D4D4D8;
      transform: translateY(-3px);
      box-shadow: 0 8px 24px rgba(0,0,0,0.35);
    }

    .quick-link .ico { font-size: 20px; line-height: 1; }

    .footer {
      position: fixed; bottom: 16px;
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
        <svg class=""g-logo"" viewBox=""0 0 24 24"" xmlns=""http://www.w3.org/2000/svg"">
          <path d=""M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92c-.26 1.37-1.04 2.53-2.21 3.31v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.09z"" fill=""#4285F4""/>
          <path d=""M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z"" fill=""#34A853""/>
          <path d=""M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.07H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.93l3.66-2.84z"" fill=""#FBBC05""/>
          <path d=""M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.07l3.66 2.84c.87-2.6 3.3-4.53 6.16-4.53z"" fill=""#EA4335""/>
        </svg>
        <input type=""text"" placeholder=""Buscar en Google o escribir una URL..."" id=""si"" autocomplete=""off"" spellcheck=""false""/>
        <button class=""search-btn"" onclick=""go()"">Buscar</button>
      </div>
    </div>

    <div class=""quick-links"">
      <a class=""quick-link"" href=""https://www.youtube.com""><div class=""ico"">▶️</div>YouTube</a>
      <a class=""quick-link"" href=""https://www.google.com""><div class=""ico"">🌐</div>Google</a>
      <a class=""quick-link"" href=""https://mail.google.com""><div class=""ico"">📧</div>Gmail</a>
      <a class=""quick-link"" href=""https://calendar.google.com""><div class=""ico"">📅</div>Calendar</a>
      <a class=""quick-link"" href=""https://drive.google.com""><div class=""ico"">💾</div>Drive</a>
      <a class=""quick-link"" href=""https://www.twitch.tv""><div class=""ico"">🎮</div>Twitch</a>
      <a class=""quick-link"" href=""https://twitter.com""><div class=""ico"">𝕏</div>X</a>
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
    setInterval(tick, 1000);

    function go() {
      const v = document.getElementById('si').value.trim();
      if (!v) return;
      if (/^https?:\/\//i.test(v)) {
        window.location.href = v;
      } else if (/^[a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?(\.[a-zA-Z]{2,})+/i.test(v) && !v.includes(' ')) {
        window.location.href = 'https://' + v;
      } else {
        window.location.href = 'https://www.google.com/search?q=' + encodeURIComponent(v);
      }
    }

    document.getElementById('si').addEventListener('keydown', function(e) {
      if (e.key === 'Enter') go();
    });
    setTimeout(function() {
      document.getElementById('si').focus();
    }, 150);
  </script>
</body>
</html>";
    }
}
