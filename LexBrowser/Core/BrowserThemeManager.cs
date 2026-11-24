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
        if (string.IsNullOrWhiteSpace(theme)) return Classic;
        string t = theme.Trim();
        if (t.Equals("Dark", StringComparison.OrdinalIgnoreCase) || t.Equals("Oscuro", StringComparison.OrdinalIgnoreCase))
            return Dark;
        if (t.Equals("Light", StringComparison.OrdinalIgnoreCase) || t.Equals("Claro", StringComparison.OrdinalIgnoreCase))
            return Light;
        return Classic;
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
                else if (win is UI.Views.HistoryWindow hw)
                {
                    hw.UpdateThemeStyles(target);
                }
                else if (win is UI.Views.PrivacyProfileSetupWindow pw)
                {
                    pw.UpdateThemeStyles(target);
                }
                else if (win is UI.Views.SettingsRecoveryDialog rw)
                {
                    rw.UpdateThemeStyles(target);
                }
                else if (win is UI.Views.VersionDowngradeDialog vw)
                {
                    vw.UpdateThemeStyles(target);
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

        ApplyBadgeAndSecondaryPanelPalette(res, theme);
    }

    public static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush CreateFrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public static Brush GetThemeBrush(FrameworkElement? element, string key, Brush fallback)
    {
        if (element != null && element.TryFindResource(key) is Brush localBrush)
            return localBrush;

        if (Application.Current?.TryFindResource(key) is Brush appBrush)
            return appBrush;

        return fallback;
    }

    public static string ApplyThemeToWindow(Window window, string? theme = null)
    {
        string target = NormalizeTheme(theme ?? SettingsManager.Current?.Theme);
        ApplyThemePalette(window.Resources, target);
        if (Application.Current != null)
        {
            ApplyThemePalette(Application.Current.Resources, target);
        }
        return target;
    }

    private static void ApplyBadgeAndSecondaryPanelPalette(ResourceDictionary res, string theme)
    {
        bool isLight = theme == Light;
        bool isClassic = theme == Classic;

        // Badges / píldoras dinámicas de la barra superior y marcadores de MainWindow
        SetBrush(res, "ShieldBadgeBg", isLight ? Color.FromRgb(0xEF, 0xF6, 0xFF) : Color.FromRgb(0x1E, 0x29, 0x3B));
        SetBrush(res, "ShieldBadgeHoverBg", isLight ? Color.FromRgb(0xDB, 0xEA, 0xFE) : Color.FromRgb(0x27, 0x35, 0x49));
        SetBrush(res, "ShieldBadgeBorder", isLight ? Color.FromRgb(0xBF, 0xDB, 0xFE) : Color.FromRgb(0x2A, 0x3A, 0x52));
        SetBrush(res, "ShieldBadgeActiveFg", isLight ? Color.FromRgb(0x03, 0x69, 0xA1) : Color.FromRgb(0x38, 0xBD, 0xF8));
        SetBrush(res, "ShieldBadgeCompatFg", isLight ? Color.FromRgb(0xB4, 0x53, 0x09) : Color.FromRgb(0xF5, 0x9E, 0x0B));
        SetBrush(res, "ShieldBadgeOffFg", isLight ? Color.FromRgb(0x47, 0x55, 0x69) : Color.FromRgb(0x9C, 0xA3, 0xAF));

        SetBrush(res, "TelemetryBadgeBg", isLight ? Color.FromRgb(0xF1, 0xF5, 0xF9) : Color.FromRgb(0x16, 0x16, 0x1D));
        SetBrush(res, "TelemetryBadgeFg", isLight ? Color.FromRgb(0x0F, 0x17, 0x2A) : Color.FromRgb(0xA1, 0xA1, 0xAA));
        SetBrush(res, "TelemetryDotFg", isLight ? Color.FromRgb(0x05, 0x96, 0x69) : Color.FromRgb(0x10, 0xB9, 0x81));

        SetBrush(res, "BookmarkActiveBg", isLight ? Color.FromRgb(0xEF, 0xF6, 0xFF) : Color.FromRgb(0x22, 0x22, 0x38));
        SetBrush(res, "VolumeBoostedBg", isLight ? Color.FromRgb(0xE0, 0xE7, 0xFF) : Color.FromRgb(0x31, 0x2E, 0x81));
        SetBrush(res, "VolumeBoostedFg", isLight ? Color.FromRgb(0x37, 0x30, 0xA3) : Color.FromRgb(0xF4, 0xF4, 0xF5));
        SetBrush(res, "SnifferActiveBg", isLight ? Color.FromRgb(0xD1, 0xFA, 0xE5) : Color.FromRgb(0x1E, 0x3A, 0x3A));
        SetBrush(res, "SnifferActiveFg", isLight ? Color.FromRgb(0x06, 0x5F, 0x46) : Color.FromRgb(0xF4, 0xF4, 0xF5));
        SetBrush(res, "SnifferActiveDot", isLight ? Color.FromRgb(0x05, 0x96, 0x69) : Color.FromRgb(0x10, 0xB9, 0x81));

        // Botón ✓ Ya es predeterminado y estado maestro del historial
        SetBrush(res, "DefaultBtnDoneBg", isLight ? Color.FromRgb(0xD1, 0xFA, 0xE5) : Color.FromRgb(0x06, 0x5F, 0x46));
        SetBrush(res, "DefaultBtnDoneFg", isLight ? Color.FromRgb(0x06, 0x5F, 0x46) : Color.FromRgb(0xEC, 0xFD, 0xF5));
        SetBrush(res, "DefaultBtnDoneBorder", isLight ? Color.FromRgb(0x6E, 0xE7, 0xB7) : Color.FromRgb(0x10, 0xB9, 0x81));

        SetBrush(res, "HistoryStatusActiveBg", isLight ? Color.FromRgb(0xDB, 0xEA, 0xFE) : Color.FromRgb(30, 58, 138));
        SetBrush(res, "HistoryStatusActiveFg", isLight ? Color.FromRgb(0x1D, 0x4E, 0xD8) : Color.FromRgb(147, 197, 253));
        SetBrush(res, "HistoryStatusDisabledBg", isLight ? Color.FromRgb(0xFE, 0xE2, 0xE2) : Color.FromRgb(76, 29, 39));
        SetBrush(res, "HistoryStatusDisabledFg", isLight ? Color.FromRgb(0xB9, 0x1C, 0x1C) : Color.FromRgb(253, 164, 175));

        if (isLight)
        {
            SetBrush(res, "PanelWindowBg", Color.FromRgb(0xF8, 0xFA, 0xFC));
            SetBrush(res, "PanelChromeBg", Color.FromRgb(0xFF, 0xFF, 0xFF));
            SetBrush(res, "PanelSidebarBg", Color.FromRgb(0xF1, 0xF5, 0xF9));
            SetBrush(res, "PanelFilterBarBg", Color.FromRgb(0xF1, 0xF5, 0xF9));
            SetBrush(res, "PanelBannerBg", Color.FromRgb(0xF8, 0xFA, 0xFC));
            SetBrush(res, "PanelListBg", Color.FromRgb(0xFF, 0xFF, 0xFF));
            SetBrush(res, "PanelCardBg", Color.FromRgb(0xFF, 0xFF, 0xFF));
            SetBrush(res, "PanelCardAltBg", Color.FromRgb(0xFF, 0xFF, 0xFF));
            SetBrush(res, "PanelCardSubtleBg", Color.FromRgb(0xFF, 0xFF, 0xFF));
            SetBrush(res, "PanelCardInsetBg", Color.FromRgb(0xF8, 0xFA, 0xFC));
            SetBrush(res, "PanelSurfaceDeepBg", Color.FromRgb(0xF8, 0xFA, 0xFC));
            SetBrush(res, "PanelRowBg", Color.FromRgb(0xF8, 0xFA, 0xFC));
            SetBrush(res, "PanelCustomTimeBg", Color.FromRgb(0xF8, 0xFA, 0xFC));
            SetBrush(res, "PanelCustomTimeBorder", Color.FromRgb(0xCB, 0xD5, 0xE1));
            SetBrush(res, "PanelBorder", Color.FromRgb(0xCB, 0xD5, 0xE1));
            SetBrush(res, "PanelBorderSubtle", Color.FromRgb(0xE2, 0xE8, 0xF0));
            SetBrush(res, "PanelBorderHairline", Color.FromRgb(0xCB, 0xD5, 0xE1));
            SetBrush(res, "PanelBorderCardAlt", Color.FromRgb(0xCB, 0xD5, 0xE1));
            SetBrush(res, "PanelBorderMuted", Color.FromRgb(0xCB, 0xD5, 0xE1));
            SetBrush(res, "PanelRowSeparator", Color.FromRgb(0xE2, 0xE8, 0xF0));
            SetBrush(res, "PanelRowHoverBg", Color.FromRgb(0xF1, 0xF5, 0xF9));
            SetBrush(res, "PanelRowSelectedBg", Color.FromRgb(0xEF, 0xF6, 0xFF));
            SetBrush(res, "PanelFaviconBg", Color.FromRgb(0xF1, 0xF5, 0xF9));
            SetBrush(res, "PanelDomainBadgeBg", Color.FromRgb(0xDB, 0xEA, 0xFE));
            SetBrush(res, "PanelTextStrong", Color.FromRgb(0x0F, 0x17, 0x2A));
            SetBrush(res, "PanelTextPrimary", Color.FromRgb(0x1E, 0x29, 0x3B));
            SetBrush(res, "PanelTextBody", Color.FromRgb(0x33, 0x41, 0x55));
            SetBrush(res, "PanelTextSoft", Color.FromRgb(0x33, 0x41, 0x55));
            SetBrush(res, "PanelTextMuted", Color.FromRgb(0x47, 0x55, 0x69));
            SetBrush(res, "PanelTextSubtle", Color.FromRgb(0x64, 0x74, 0x8B));
            SetBrush(res, "PanelAccentBg", Color.FromRgb(0x25, 0x63, 0xEB));
            SetBrush(res, "PanelAccentHoverBg", Color.FromRgb(0x1D, 0x4E, 0xD8));
            SetBrush(res, "PanelAccentFg", Color.FromRgb(0x1D, 0x4E, 0xD8));
            SetBrush(res, "PanelTabHoverBg", Color.FromRgb(0xE2, 0xE8, 0xF0));
            SetBrush(res, "PanelTabActiveBg", Color.FromRgb(0xDB, 0xEA, 0xFE));
            SetBrush(res, "PanelHistoryBtnBg", Color.FromRgb(0xEF, 0xF6, 0xFF));
            SetBrush(res, "PanelHistoryBtnHoverBg", Color.FromRgb(0xDB, 0xEA, 0xFE));
            SetBrush(res, "PanelFilterPillBg", Color.FromRgb(0xF1, 0xF5, 0xF9));
            SetBrush(res, "PanelFilterPillBorder", Color.FromRgb(0xCB, 0xD5, 0xE1));
            SetBrush(res, "PanelControlBg", Color.FromRgb(0xFF, 0xFF, 0xFF));
            SetBrush(res, "PanelControlHoverBg", Color.FromRgb(0xF1, 0xF5, 0xF9));
            SetBrush(res, "PanelControlFocusedBg", Color.FromRgb(0xFF, 0xFF, 0xFF));
            SetBrush(res, "PanelControlBorder", Color.FromRgb(0x94, 0xA3, 0xB8));
            SetBrush(res, "PanelControlCheckBorder", Color.FromRgb(0x64, 0x74, 0x8B));
            SetBrush(res, "PanelComboPopupBg", Color.FromRgb(0xFF, 0xFF, 0xFF));
            SetBrush(res, "PanelComboSelectedBg", Color.FromRgb(0x25, 0x63, 0xEB));
            SetBrush(res, "PanelToggleTrackBg", Color.FromRgb(0xCB, 0xD5, 0xE1));
            SetBrush(res, "PanelRadioActiveBg", Color.FromRgb(0xEF, 0xF6, 0xFF));
            SetBrush(res, "PanelScrollThumbBg", Color.FromRgb(0xCB, 0xD5, 0xE1));
            SetBrush(res, "PanelScrollThumbHoverBg", Color.FromRgb(0x94, 0xA3, 0xB8));
            SetBrush(res, "PanelScrollThumbDragBg", Color.FromRgb(0x64, 0x74, 0x8B));
            SetBrush(res, "PanelActionBtnBg", Color.FromRgb(0xF1, 0xF5, 0xF9));
            SetBrush(res, "PanelDangerFg", Color.FromRgb(0xDC, 0x26, 0x26));
            SetBrush(res, "PanelSuccessFg", Color.FromRgb(0x05, 0x96, 0x69));
            SetBrush(res, "PanelWarningFg", Color.FromRgb(0xD9, 0x77, 0x06));
            SetLinearGradient(res, "PanelTelemetryBg", Color.FromRgb(0xEF, 0xF6, 0xFF), Color.FromRgb(0xFF, 0xFF, 0xFF));
            SetBrush(res, "PanelTelemetryBorder", Color.FromRgb(0x3B, 0x82, 0xF6));
            SetBrush(res, "PanelTelemetryPillBg", Color.FromRgb(0xDB, 0xEA, 0xFE));
            SetBrush(res, "PanelTelemetryPillFg", Color.FromRgb(0x1D, 0x4E, 0xD8));
            SetBrush(res, "PanelSiteCardBg", Color.FromRgb(0xFF, 0xFF, 0xFF));
            SetBrush(res, "PanelSiteCardBorder", Color.FromRgb(0xCB, 0xD5, 0xE1));
            SetBrush(res, "PanelSiteIconBg", Color.FromRgb(0xF1, 0xF5, 0xF9));
            SetBrush(res, "PanelBadgeBlockBg", Color.FromRgb(0xFE, 0xE2, 0xE2));
            SetBrush(res, "PanelBadgeBlockFg", Color.FromRgb(0xB9, 0x1C, 0x1C));
            SetBrush(res, "PanelBadgeAllowBg", Color.FromRgb(0xD1, 0xFA, 0xE5));
            SetBrush(res, "PanelBadgeAllowFg", Color.FromRgb(0x06, 0x5F, 0x46));
            SetBrush(res, "PanelBadgeSilentBg", Color.FromRgb(0xED, 0xE9, 0xFE));
            SetBrush(res, "PanelBadgeSilentFg", Color.FromRgb(0x6D, 0x28, 0xD9));
            SetBrush(res, "PanelBadgeAskBg", Color.FromRgb(0xF1, 0xF5, 0xF9));
            SetBrush(res, "PanelBadgeAskFg", Color.FromRgb(0x33, 0x41, 0x55));
            SetBrush(res, "PanelDrawerBg", Color.FromRgb(0xF8, 0xFA, 0xFC));
            SetBrush(res, "PanelDrawerBorder", Color.FromRgb(0x93, 0xC5, 0xFD));
            SetBrush(res, "PanelOnAccentFg", Color.FromRgb(0xFF, 0xFF, 0xFF));
            SetBrush(res, "PanelWarningBadgeBg", Color.FromRgb(0xFE, 0xF3, 0xC7));
            SetBrush(res, "PanelWarningBadgeFg", Color.FromRgb(0xB4, 0x53, 0x09));
            SetBrush(res, "PanelCloseHoverBg", Color.FromRgb(0xE1, 0x1D, 0x48));
            SetBrush(res, "PanelCloseHoverFg", Color.FromRgb(0xFF, 0xFF, 0xFF));
            return;
        }

        // Dark y Classic conservan la estética oscura original (Classic con acento naranja #FF6B00)
        SetBrush(res, "PanelWindowBg", isClassic ? Color.FromRgb(0x0A, 0x0A, 0x0C) : Color.FromRgb(0x0F, 0x0F, 0x14));
        SetBrush(res, "PanelChromeBg", isClassic ? Color.FromRgb(0x10, 0x10, 0x12) : Color.FromRgb(0x14, 0x14, 0x1A));
        SetBrush(res, "PanelSidebarBg", isClassic ? Color.FromRgb(0x0D, 0x0D, 0x0F) : Color.FromRgb(0x12, 0x12, 0x17));
        SetBrush(res, "PanelFilterBarBg", isClassic ? Color.FromRgb(0x0D, 0x0D, 0x0F) : Color.FromRgb(0x11, 0x11, 0x16));
        SetBrush(res, "PanelBannerBg", isClassic ? Color.FromRgb(0x12, 0x12, 0x15) : Color.FromRgb(0x16, 0x16, 0x1F));
        SetBrush(res, "PanelListBg", isClassic ? Color.FromRgb(0x08, 0x08, 0x0A) : Color.FromRgb(0x0C, 0x0C, 0x10));
        SetBrush(res, "PanelCardBg", isClassic ? Color.FromRgb(0x12, 0x12, 0x15) : Color.FromRgb(0x15, 0x15, 0x1D));
        SetBrush(res, "PanelCardAltBg", isClassic ? Color.FromRgb(0x12, 0x12, 0x15) : Color.FromRgb(0x15, 0x15, 0x1F));
        SetBrush(res, "PanelCardSubtleBg", isClassic ? Color.FromRgb(0x16, 0x16, 0x1A) : Color.FromRgb(0x1A, 0x1A, 0x24));
        SetBrush(res, "PanelCardInsetBg", isClassic ? Color.FromRgb(0x0F, 0x0F, 0x12) : Color.FromRgb(0x12, 0x12, 0x18));
        SetBrush(res, "PanelSurfaceDeepBg", isClassic ? Color.FromRgb(0x0D, 0x0D, 0x10) : Color.FromRgb(0x10, 0x10, 0x16));
        SetBrush(res, "PanelRowBg", isClassic ? Color.FromRgb(0x16, 0x16, 0x1A) : Color.FromRgb(0x19, 0x19, 0x24));
        SetBrush(res, "PanelCustomTimeBg", isClassic ? Color.FromRgb(0x18, 0x18, 0x1D) : Color.FromRgb(0x1B, 0x1B, 0x26));
        SetBrush(res, "PanelCustomTimeBorder", isClassic ? Color.FromRgb(0x2C, 0x2C, 0x30) : Color.FromRgb(0x2C, 0x2C, 0x40));
        SetBrush(res, "PanelBorder", isClassic ? Color.FromRgb(0x28, 0x28, 0x2C) : Color.FromRgb(0x27, 0x27, 0x36));
        SetBrush(res, "PanelBorderSubtle", isClassic ? Color.FromRgb(0x24, 0x24, 0x28) : Color.FromRgb(0x24, 0x24, 0x30));
        SetBrush(res, "PanelBorderHairline", isClassic ? Color.FromRgb(0x22, 0x22, 0x26) : Color.FromRgb(0x20, 0x20, 0x2B));
        SetBrush(res, "PanelBorderCardAlt", isClassic ? Color.FromRgb(0x28, 0x28, 0x2C) : Color.FromRgb(0x25, 0x25, 0x38));
        SetBrush(res, "PanelBorderMuted", Color.FromRgb(0x1F, 0x29, 0x37));
        SetBrush(res, "PanelRowSeparator", Color.FromRgb(0x1A, 0x1A, 0x24));
        SetBrush(res, "PanelRowHoverBg", isClassic ? Color.FromRgb(0x18, 0x18, 0x1D) : Color.FromRgb(0x17, 0x17, 0x22));
        SetBrush(res, "PanelRowSelectedBg", isClassic ? Color.FromRgb(0x22, 0x1A, 0x14) : Color.FromRgb(0x1E, 0x1E, 0x2E));
        SetBrush(res, "PanelFaviconBg", Color.FromRgb(0x1C, 0x1C, 0x26));
        SetBrush(res, "PanelDomainBadgeBg", isClassic ? Color.FromRgb(0x26, 0x19, 0x10) : Color.FromRgb(0x20, 0x20, 0x2F));
        SetBrush(res, "PanelTextStrong", Color.FromRgb(0xFF, 0xFF, 0xFF));
        SetBrush(res, "PanelTextPrimary", Color.FromRgb(0xF4, 0xF4, 0xF5));
        SetBrush(res, "PanelTextBody", Color.FromRgb(0xE4, 0xE4, 0xE7));
        SetBrush(res, "PanelTextSoft", Color.FromRgb(0xD4, 0xD4, 0xD8));
        SetBrush(res, "PanelTextMuted", Color.FromRgb(0xA1, 0xA1, 0xAA));
        SetBrush(res, "PanelTextSubtle", Color.FromRgb(0x8B, 0x8B, 0x96));
        SetBrush(res, "PanelAccentBg", isClassic ? Color.FromRgb(0xFF, 0x6B, 0x00) : Color.FromRgb(0x4F, 0x46, 0xE5));
        SetBrush(res, "PanelAccentHoverBg", isClassic ? Color.FromRgb(0xE0, 0x5E, 0x00) : Color.FromRgb(0x43, 0x38, 0xCA));
        SetBrush(res, "PanelAccentFg", isClassic ? Color.FromRgb(0xFB, 0x92, 0x3C) : Color.FromRgb(0x81, 0x8C, 0xF8));
        SetBrush(res, "PanelTabHoverBg", Color.FromRgb(0x1E, 0x1E, 0x28));
        SetBrush(res, "PanelTabActiveBg", isClassic ? Color.FromRgb(0x26, 0x19, 0x10) : Color.FromRgb(0x25, 0x25, 0x38));
        SetBrush(res, "PanelHistoryBtnBg", isClassic ? Color.FromRgb(0x22, 0x1B, 0x15) : Color.FromRgb(0x24, 0x24, 0x36));
        SetBrush(res, "PanelHistoryBtnHoverBg", isClassic ? Color.FromRgb(0x30, 0x24, 0x1B) : Color.FromRgb(0x30, 0x30, 0x48));
        SetBrush(res, "PanelFilterPillBg", Color.FromRgb(0x1C, 0x1C, 0x28));
        SetBrush(res, "PanelFilterPillBorder", Color.FromRgb(0x2D, 0x2D, 0x3E));
        SetBrush(res, "PanelControlBg", Color.FromRgb(0x17, 0x17, 0x20));
        SetBrush(res, "PanelControlHoverBg", Color.FromRgb(0x26, 0x26, 0x38));
        SetBrush(res, "PanelControlFocusedBg", Color.FromRgb(0x1C, 0x1C, 0x28));
        SetBrush(res, "PanelControlBorder", Color.FromRgb(0x2E, 0x2E, 0x3E));
        SetBrush(res, "PanelControlCheckBorder", Color.FromRgb(0x3B, 0x3B, 0x4E));
        SetBrush(res, "PanelComboPopupBg", Color.FromRgb(0x14, 0x14, 0x1E));
        SetBrush(res, "PanelComboSelectedBg", isClassic ? Color.FromRgb(0xFF, 0x6B, 0x00) : Color.FromRgb(0x4F, 0x46, 0xE5));
        SetBrush(res, "PanelToggleTrackBg", Color.FromRgb(0x2E, 0x2E, 0x3A));
        SetBrush(res, "PanelRadioActiveBg", isClassic ? Color.FromRgb(0x2A, 0x1A, 0x10) : Color.FromRgb(0x1E, 0x1E, 0x30));
        SetBrush(res, "PanelScrollThumbBg", Color.FromRgb(0x38, 0x38, 0x4E));
        SetBrush(res, "PanelScrollThumbHoverBg", isClassic ? Color.FromRgb(0xE0, 0x5E, 0x00) : Color.FromRgb(0x63, 0x66, 0xF1));
        SetBrush(res, "PanelScrollThumbDragBg", isClassic ? Color.FromRgb(0xFF, 0x6B, 0x00) : Color.FromRgb(0x81, 0x8C, 0xF8));
        SetBrush(res, "PanelActionBtnBg", Color.FromRgb(0x22, 0x22, 0x2E));
        SetBrush(res, "PanelDangerFg", Color.FromRgb(0xFB, 0x71, 0x85));
        SetBrush(res, "PanelSuccessFg", Color.FromRgb(0x10, 0xB9, 0x81));
        SetBrush(res, "PanelWarningFg", Color.FromRgb(0xF5, 0x9E, 0x0B));
        SetLinearGradient(res, "PanelTelemetryBg",
            isClassic ? Color.FromRgb(0x2B, 0x17, 0x0C) : Color.FromRgb(0x2C, 0x1B, 0x4D),
            Color.FromRgb(0x15, 0x15, 0x1E));
        SetBrush(res, "PanelTelemetryBorder", isClassic ? Color.FromRgb(0xFF, 0x6B, 0x00) : Color.FromRgb(0x4F, 0x46, 0xE5));
        SetBrush(res, "PanelTelemetryPillBg", isClassic ? Color.FromArgb(0x24, 0xFF, 0x6B, 0x00) : Color.FromArgb(0x20, 0x63, 0x66, 0xF1));
        SetBrush(res, "PanelTelemetryPillFg", isClassic ? Color.FromRgb(0xFD, 0xBA, 0x74) : Color.FromRgb(0xA5, 0xB4, 0xFC));
        SetBrush(res, "PanelSiteCardBg", Color.FromRgb(0x18, 0x18, 0x24));
        SetBrush(res, "PanelSiteCardBorder", Color.FromRgb(0x29, 0x29, 0x3C));
        SetBrush(res, "PanelSiteIconBg", Color.FromRgb(0x23, 0x23, 0x34));
        SetBrush(res, "PanelBadgeBlockBg", Color.FromRgb(0x3B, 0x16, 0x1B));
        SetBrush(res, "PanelBadgeBlockFg", Color.FromRgb(0xF8, 0x71, 0x71));
        SetBrush(res, "PanelBadgeAllowBg", Color.FromRgb(0x0E, 0x33, 0x21));
        SetBrush(res, "PanelBadgeAllowFg", Color.FromRgb(0x34, 0xD3, 0x99));
        SetBrush(res, "PanelBadgeSilentBg", Color.FromRgb(0x28, 0x20, 0x48));
        SetBrush(res, "PanelBadgeSilentFg", Color.FromRgb(0xA7, 0x8B, 0xFA));
        SetBrush(res, "PanelBadgeAskBg", Color.FromRgb(0x25, 0x25, 0x33));
        SetBrush(res, "PanelBadgeAskFg", Color.FromRgb(0xD4, 0xD4, 0xD8));
        SetBrush(res, "PanelDrawerBg", Color.FromRgb(0x14, 0x14, 0x1F));
        SetBrush(res, "PanelDrawerBorder", isClassic ? Color.FromRgb(0x9A, 0x34, 0x12) : Color.FromRgb(0x31, 0x2E, 0x81));
        SetBrush(res, "PanelOnAccentFg", isClassic ? Color.FromRgb(0x0A, 0x0A, 0x0C) : Color.FromRgb(0xFF, 0xFF, 0xFF));
        SetBrush(res, "PanelWarningBadgeBg", Color.FromRgb(0x3A, 0x28, 0x0A));
        SetBrush(res, "PanelWarningBadgeFg", Color.FromRgb(0xFB, 0xBF, 0x24));
        SetBrush(res, "PanelCloseHoverBg", Color.FromRgb(0xE1, 0x1D, 0x48));
        SetBrush(res, "PanelCloseHoverFg", Color.FromRgb(0xFF, 0xFF, 0xFF));
    }

    private static void SetLinearGradient(ResourceDictionary res, string key, Color startColor, Color endColor)
    {
        var grad = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        grad.GradientStops.Add(new GradientStop(startColor, 0.0));
        grad.GradientStops.Add(new GradientStop(endColor, 0.8));
        res[key] = grad;
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

