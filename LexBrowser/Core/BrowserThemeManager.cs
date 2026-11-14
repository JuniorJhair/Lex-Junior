using System;
using System.Windows;
using System.Windows.Media;

namespace LexBrowser.Core;

public static class BrowserThemeManager
{
    public const string Dark = "Dark";
    public const string Light = "Light";
    public const string Classic = "Classic";

    public static string CurrentTheme => NormalizeTheme(SettingsManager.Current?.Theme);

    public static string NormalizeTheme(string? theme)
    {
        if (string.IsNullOrWhiteSpace(theme)) return Dark;
        string t = theme.Trim();
        if (t.Equals("Light", StringComparison.OrdinalIgnoreCase) || t.Equals("Claro", StringComparison.OrdinalIgnoreCase))
            return Light;
        if (t.Equals("Classic", StringComparison.OrdinalIgnoreCase) || t.Equals("Clasico", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Clásico", StringComparison.OrdinalIgnoreCase) || t.Equals("Original", StringComparison.OrdinalIgnoreCase))
            return Classic;
        return Dark;
    }

    public static void ApplyTheme(string? theme)
    {
        string target = NormalizeTheme(theme);

        if (Application.Current == null) return;

        Action action = () =>
        {
            ApplyThemePalette(Application.Current.Resources, target);

            foreach (Window win in Application.Current.Windows)
            {
                ApplyThemePalette(win.Resources, target);
                if (win is MainWindow mw)
                {
                    mw.UpdateThemeStyles(target);
                }
                else if (win is UI.Views.SettingsWindow sw)
                {
                    sw.UpdateThemeStyles(target);
                }
            }
        };

        if (Application.Current.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Application.Current.Dispatcher.Invoke(action);
        }
    }

    public static void ApplyThemePalette(ResourceDictionary res, string theme)
    {
        if (res == null) return;

        // Dark Palette (Predeterminado)
        Color bgDark = Color.FromRgb(0x0F, 0x0F, 0x12);
        Color surfaceDark = Color.FromRgb(0x18, 0x18, 0x1D);
        Color surfaceLighter = Color.FromRgb(0x22, 0x22, 0x2B);
        Color borderDark = Color.FromRgb(0x2E, 0x2E, 0x3A);
        Color textLight = Color.FromRgb(0xF4, 0xF4, 0xF5);
        Color textMuted = Color.FromRgb(0xA1, 0xA1, 0xAA);
        Color accentColor = Color.FromRgb(0x4F, 0x46, 0xE5);
        Color accentHover = Color.FromRgb(0x81, 0x8C, 0xF8);
        Color tabInactiveBg = Color.FromRgb(0x16, 0x16, 0x1B);
        Color tabHoverBg = Color.FromRgb(0x25, 0x25, 0x35);
        Color bookmarksBarBg = Color.FromRgb(0x12, 0x12, 0x17);
        Color bookmarkItemBg = Color.FromRgb(0x18, 0x18, 0x20);
        Color utilityBtnBg = Color.FromRgb(0x25, 0x25, 0x36);
        Color caretColor = Color.FromRgb(0xFF, 0xFF, 0xFF);
        Color windowCtrlHoverBg = Color.FromRgb(0x25, 0x25, 0x30);

        if (theme == Light)
        {
            bgDark = Color.FromRgb(0xE2, 0xE8, 0xF0);
            surfaceDark = Color.FromRgb(0xFF, 0xFF, 0xFF);
            surfaceLighter = Color.FromRgb(0xF1, 0xF5, 0xF9);
            borderDark = Color.FromRgb(0xCB, 0xD5, 0xE1);
            textLight = Color.FromRgb(0x0F, 0x17, 0x2A);
            textMuted = Color.FromRgb(0x64, 0x74, 0x8B);
            accentColor = Color.FromRgb(0x25, 0x63, 0xEB);
            accentHover = Color.FromRgb(0x1D, 0x4E, 0xD8);
            tabInactiveBg = Color.FromRgb(0xEA, 0xEE, 0xF4);
            tabHoverBg = Color.FromRgb(0xF8, 0xFA, 0xFC);
            bookmarksBarBg = Color.FromRgb(0xF8, 0xFA, 0xFC);
            bookmarkItemBg = Color.FromRgb(0xFF, 0xFF, 0xFF);
            utilityBtnBg = Color.FromRgb(0xF1, 0xF5, 0xF9);
            caretColor = Color.FromRgb(0x0F, 0x17, 0x2A);
            windowCtrlHoverBg = Color.FromRgb(0xCB, 0xD5, 0xE1);
        }
        else if (theme == Classic)
        {
            bgDark = Color.FromRgb(0x00, 0x00, 0x00);
            surfaceDark = Color.FromRgb(0x11, 0x11, 0x11);
            surfaceLighter = Color.FromRgb(0x18, 0x18, 0x18);
            borderDark = Color.FromRgb(0x2C, 0x2C, 0x2C);
            textLight = Color.FromRgb(0xFF, 0xFF, 0xFF);
            textMuted = Color.FromRgb(0x9E, 0x9E, 0x9E);
            accentColor = Color.FromRgb(0xFF, 0x6B, 0x00);
            accentHover = Color.FromRgb(0xE0, 0x5E, 0x00);
            tabInactiveBg = Color.FromRgb(0x08, 0x08, 0x08);
            tabHoverBg = Color.FromRgb(0x1C, 0x1C, 0x1C);
            bookmarksBarBg = Color.FromRgb(0x06, 0x06, 0x06);
            bookmarkItemBg = Color.FromRgb(0x14, 0x14, 0x14);
            utilityBtnBg = Color.FromRgb(0x18, 0x18, 0x18);
            caretColor = Color.FromRgb(0xFF, 0x6B, 0x00);
            windowCtrlHoverBg = Color.FromRgb(0x1C, 0x1C, 0x1C);
        }

        SetBrush(res, "BgDark", bgDark);
        SetBrush(res, "SurfaceDark", surfaceDark);
        SetBrush(res, "SurfaceLighter", surfaceLighter);
        SetBrush(res, "BorderDark", borderDark);
        SetBrush(res, "TextLight", textLight);
        SetBrush(res, "TextMuted", textMuted);
        SetBrush(res, "AccentColor", accentColor);
        SetBrush(res, "AccentHover", accentHover);
        SetBrush(res, "TabInactiveBg", tabInactiveBg);
        SetBrush(res, "TabHoverBg", tabHoverBg);
        SetBrush(res, "BookmarksBarBg", bookmarksBarBg);
        SetBrush(res, "BookmarkItemBg", bookmarkItemBg);
        SetBrush(res, "UtilityBtnBg", utilityBtnBg);
        SetBrush(res, "CaretColor", caretColor);
        SetBrush(res, "WindowCtrlHoverBg", windowCtrlHoverBg);

        // Claves compartidas para controles de Theme.xaml
        SetBrush(res, "BrushAppBackground", bgDark);
        SetBrush(res, "BrushWindowBackground", surfaceDark);
        SetBrush(res, "BrushSurfaceDark", surfaceDark);
        SetBrush(res, "BrushSurfaceMedium", surfaceLighter);
        SetBrush(res, "BrushSurfaceCard", tabInactiveBg);
        SetBrush(res, "BrushBorderDefault", borderDark);
        SetBrush(res, "BrushBorderSubtle", borderDark);
        SetBrush(res, "BrushTextTitle", textLight);
        SetBrush(res, "BrushTextSubtitle", textMuted);
        SetBrush(res, "BrushTextSecondary", textMuted);
    }

    private static void SetBrush(ResourceDictionary res, string key, Color color)
    {
        if (res.Contains(key) && res[key] is SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.Color = color;
        }
        else
        {
            res[key] = new SolidColorBrush(color);
        }
    }
}

