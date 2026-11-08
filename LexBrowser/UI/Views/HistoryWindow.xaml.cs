using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using LexBrowser.Core;

namespace LexBrowser.UI.Views;

public partial class HistoryWindow : Window
{
    private static readonly SolidColorBrush BrushStatusActiveBg = CreateFrozenBrush(30, 58, 138);
    private static readonly SolidColorBrush BrushStatusActiveFg = CreateFrozenBrush(147, 197, 253);
    private static readonly SolidColorBrush BrushStatusDisabledBg = CreateFrozenBrush(76, 29, 39);
    private static readonly SolidColorBrush BrushStatusDisabledFg = CreateFrozenBrush(253, 164, 175);

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private readonly CoreWebView2? _core;
    private bool _isInitialized = false;
    private bool _suppressFilterEvents = false;
    private string? _currentDomainFilter = null;
    private DateTime? _fromDateFilter = null;
    private DateTime? _toDateFilter = null;
    private int _currentPage = 1;
    private int _pageSize = 50;
    private int _totalPages = 1;
    private List<HistoryEntry> _allFilteredEntries = new();

    private CancellationTokenSource? _searchDebounceCts;
    private int _refreshVersion = 0;

    public event Action<string>? UrlSelected;

    public HistoryWindow(CoreWebView2? core = null)
    {
        _isInitialized = false;
        InitializeComponent();
        _core = core;
        _isInitialized = true;
        Loaded += HistoryWindow_Loaded;
        Closed += (_, _) =>
        {
            _searchDebounceCts?.Cancel();
            _searchDebounceCts?.Dispose();
        };
    }

    private async void HistoryWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateHistoryMasterStatusUI();
        LoadDomainFilters();
        await RefreshHistoryListAsync();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
        }
        else
        {
            DragMove();
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // ─────────────────────────────────────────────────────────────────
    // INTERRUPTOR MAESTRO: ACTIVAR / DESACTIVAR HISTORIAL
    // ─────────────────────────────────────────────────────────────────
    private void UpdateHistoryMasterStatusUI()
    {
        if (!_isInitialized || ToggleHistoryActive == null || BadgeHistoryStatus == null || TxtHistoryStatus == null || TxtStorageInfo == null) return;

        bool isEnabled = SettingsManager.Current.EnableHistory;
        ToggleHistoryActive.IsChecked = isEnabled;

        if (isEnabled)
        {
            BadgeHistoryStatus.Background = BrushStatusActiveBg;
            TxtHistoryStatus.Text = "ACTIVO";
            TxtHistoryStatus.Foreground = BrushStatusActiveFg;
        }
        else
        {
            BadgeHistoryStatus.Background = BrushStatusDisabledBg;
            TxtHistoryStatus.Text = "DESACTIVADO (0 ESPACIO)";
            TxtHistoryStatus.Foreground = BrushStatusDisabledFg;
        }

        TxtStorageInfo.Text = $"Espacio en disco: {HistoryManager.GetHistoryFileSizeFormatted()} ({HistoryManager.GetTotalCount()} visitas)";
    }

    private async void ToggleHistoryActive_Click(object sender, RoutedEventArgs e)
    {
        bool requestingActive = ToggleHistoryActive.IsChecked ?? true;

        if (!requestingActive)
        {
            int total = HistoryManager.GetTotalCount();
            if (total > 0)
            {
                var dialog = new HistoryDisablePromptDialog { Owner = this };
                bool? dialogResult = dialog.ShowDialog();

                if (dialogResult == true)
                {
                    if (dialog.Result == HistoryDisableChoice.DeleteAndDisable)
                    {
                        HistoryManager.ClearAll();
                        SettingsManager.Current.EnableHistory = false;
                        SettingsManager.Save();
                        LoadDomainFilters();
                        await RefreshHistoryListAsync();
                    }
                    else if (dialog.Result == HistoryDisableChoice.KeepAndDisable)
                    {
                        SettingsManager.Current.EnableHistory = false;
                        SettingsManager.Save();
                    }
                }
                else
                {
                    ToggleHistoryActive.IsChecked = true;
                    return;
                }
            }
            else
            {
                SettingsManager.Current.EnableHistory = false;
                SettingsManager.Save();
            }
        }
        else
        {
            SettingsManager.Current.EnableHistory = true;
            SettingsManager.Save();
        }

        UpdateHistoryMasterStatusUI();
    }

    // ─────────────────────────────────────────────────────────────────
    // PESTAÑAS DE MODO (EXPLORADOR vs LIMPIEZA AVANZADA)
    // ─────────────────────────────────────────────────────────────────
    private async void ModeTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || ViewExplorer == null || ViewClearAdvanced == null) return;

        if (TabExplore.IsChecked == true)
        {
            ViewExplorer.Visibility = Visibility.Visible;
            ViewClearAdvanced.Visibility = Visibility.Collapsed;
            await RefreshHistoryListAsync();
        }
        else if (TabClearAdvanced.IsChecked == true)
        {
            ViewExplorer.Visibility = Visibility.Collapsed;
            ViewClearAdvanced.Visibility = Visibility.Visible;
            LoadDomainFilters();
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // FILTROS Y CHIPS
    // ─────────────────────────────────────────────────────────────────
    private void LoadDomainFilters()
    {
        if (!_isInitialized || CboDomainFilter == null || CboScopeTargetDomain == null || DomainChipsPanel == null) return;

        _suppressFilterEvents = true;
        try
        {
            var domains = HistoryManager.GetDistinctDomains();
            string? previousDomain = _currentDomainFilter;

            // Combo en el explorador
            CboDomainFilter.Items.Clear();
            CboDomainFilter.Items.Add("Todos los dominios");
            int selectedIndex = 0;
            int idx = 1;
            foreach (var d in domains)
            {
                CboDomainFilter.Items.Add(d.DisplayText);
                if (!string.IsNullOrEmpty(previousDomain) &&
                    d.Domain.Equals(previousDomain, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = idx;
                }
                idx++;
            }
            CboDomainFilter.SelectedIndex = selectedIndex;
            if (selectedIndex == 0) _currentDomainFilter = null;

            // Combo en la limpieza avanzada
            CboScopeTargetDomain.Items.Clear();
            foreach (var d in domains)
            {
                CboScopeTargetDomain.Items.Add(d.Domain);
            }
            if (CboScopeTargetDomain.Items.Count > 0)
            {
                CboScopeTargetDomain.SelectedIndex = 0;
            }

            // Chips rápidos
            DomainChipsPanel.Children.Clear();
            foreach (var d in domains.Take(6))
            {
                var btn = new Button
                {
                    Content = $"{d.Domain} ({d.Count})",
                    Style = (Style)FindResource("ActionBtn"),
                    Padding = new Thickness(8, 2, 8, 2),
                    Margin = new Thickness(0, 0, 6, 0),
                    Tag = d.Domain
                };
                btn.Click += (s, args) =>
                {
                    SelectDomainInFilter(d.Domain);
                };
                DomainChipsPanel.Children.Add(btn);
            }
        }
        finally
        {
            _suppressFilterEvents = false;
        }
    }

    private void SelectDomainInFilter(string domain)
    {
        if (!_isInitialized || CboDomainFilter == null) return;

        for (int i = 0; i < CboDomainFilter.Items.Count; i++)
        {
            string itemStr = CboDomainFilter.Items[i]?.ToString() ?? "";
            if (itemStr.StartsWith(domain, StringComparison.OrdinalIgnoreCase))
            {
                CboDomainFilter.SelectedIndex = i;
                return;
            }
        }
    }

    private async void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized) return;

        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;

        try
        {
            await Task.Delay(180, cts.Token);
            if (cts.IsCancellationRequested) return;
            _currentPage = 1;
            await RefreshHistoryListAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void CboDomainFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || _suppressFilterEvents || CboDomainFilter == null || CboDomainFilter.SelectedItem == null) return;
        string sel = CboDomainFilter.SelectedItem.ToString() ?? "";
        if (sel == "Todos los dominios" || string.IsNullOrWhiteSpace(sel))
        {
            _currentDomainFilter = null;
        }
        else
        {
            int idx = sel.IndexOf('(');
            _currentDomainFilter = (idx > 0 ? sel.Substring(0, idx) : sel).Trim();
        }
        _currentPage = 1;
        await RefreshHistoryListAsync();
    }

    private async void CboDateFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || _suppressFilterEvents || CboDateFilter == null) return;

        if (CboDateFilter.SelectedItem is ComboBoxItem item)
        {
            string choice = item.Content?.ToString() ?? "";
            DateTime now = DateTime.Now;

            switch (choice)
            {
                case "Hoy":
                    _fromDateFilter = now.Date;
                    _toDateFilter = now.Date.AddDays(1).AddTicks(-1);
                    break;
                case "Ayer":
                    _fromDateFilter = now.Date.AddDays(-1);
                    _toDateFilter = now.Date.AddTicks(-1);
                    break;
                case "Últimos 7 días":
                    _fromDateFilter = now.AddDays(-7);
                    _toDateFilter = null;
                    break;
                case "Últimos 30 días":
                    _fromDateFilter = now.AddDays(-30);
                    _toDateFilter = null;
                    break;
                default:
                    _fromDateFilter = null;
                    _toDateFilter = null;
                    break;
            }
            _currentPage = 1;
            await RefreshHistoryListAsync();
        }
    }

    private async Task RefreshHistoryListAsync(CancellationToken ct = default)
    {
        if (!_isInitialized || LstHistory == null || TxtHistoryCount == null) return;

        int myVersion = Interlocked.Increment(ref _refreshVersion);
        string? searchText = string.IsNullOrWhiteSpace(TxtSearch?.Text) ? null : TxtSearch.Text;
        string? domainFilter = _currentDomainFilter;
        DateTime? fromDate = _fromDateFilter;
        DateTime? toDate = _toDateFilter;
        int pageSize = _pageSize;

        var filtered = await Task.Run(
            () => HistoryManager.GetEntries(searchText, domainFilter, fromDate, toDate),
            ct);

        if (ct.IsCancellationRequested || myVersion != _refreshVersion) return;

        _allFilteredEntries = filtered;
        _totalPages = Math.Max(1, (int)Math.Ceiling(_allFilteredEntries.Count / (double)pageSize));
        if (_currentPage > _totalPages) _currentPage = _totalPages;
        if (_currentPage < 1) _currentPage = 1;

        var pageEntries = _allFilteredEntries.Skip((_currentPage - 1) * pageSize).Take(pageSize).ToList();
        LstHistory.ItemsSource = pageEntries;

        int totalCount = HistoryManager.GetTotalCount();
        TxtHistoryCount.Text = $"{_allFilteredEntries.Count} encontradas (Total: {totalCount})";

        if (TxtPageInfo != null)
        {
            TxtPageInfo.Text = $"Pág. {_currentPage} de {_totalPages}";
        }
        if (BtnPrevPage != null) BtnPrevPage.IsEnabled = _currentPage > 1;
        if (BtnNextPage != null) BtnNextPage.IsEnabled = _currentPage < _totalPages;
    }

    private async void BtnPrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage > 1)
        {
            _currentPage--;
            await RefreshHistoryListAsync();
        }
    }

    private async void BtnNextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage < _totalPages)
        {
            _currentPage++;
            await RefreshHistoryListAsync();
        }
    }

    private async void CmbPageSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || CmbPageSize == null) return;
        if (CmbPageSize.SelectedItem is ComboBoxItem item && int.TryParse(item.Content?.ToString(), out int size))
        {
            _pageSize = size;
            _currentPage = 1;
            await RefreshHistoryListAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // ACCIONES POR ELEMENTO / DÍA
    // ─────────────────────────────────────────────────────────────────
    private void HistoryRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is HistoryEntry entry)
        {
            LstHistory.SelectedItem = entry;

            // Un clic selecciona la fila (para usar "Eliminar día seleccionado"); doble clic navega y cierra
            if (e.ClickCount == 2 && !string.IsNullOrWhiteSpace(entry.Url))
            {
                e.Handled = true;
                UrlSelected?.Invoke(entry.Url);
                Close();
            }
        }
    }

    private void LstHistory_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && LstHistory.SelectedItem is HistoryEntry entry && !string.IsNullOrWhiteSpace(entry.Url))
        {
            e.Handled = true;
            UrlSelected?.Invoke(entry.Url);
            Close();
        }
    }

    private void HistoryTitle_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock tb && tb.DataContext is HistoryEntry entry && !string.IsNullOrWhiteSpace(entry.Url))
        {
            UrlSelected?.Invoke(entry.Url);
            Close();
        }
    }

    private async void BtnDeleteSingle_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button btn && btn.DataContext is HistoryEntry entry)
        {
            HistoryManager.DeleteEntry(entry.Id);
            UpdateHistoryMasterStatusUI();
            await RefreshHistoryListAsync();
        }
    }

    private async void BtnCleanSite_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button btn && btn.DataContext is HistoryEntry entry)
        {
            string domain = entry.Domain;
            var result = MessageBox.Show(
                $"¿Deseas limpiar todos los datos del sitio web '{domain}'?\n\nEsto eliminará su historial, cookies, sesiones activas y caché sin afectar a tus otras páginas.",
                "Limpiar Sitio Web Específico",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );

            if (result == MessageBoxResult.Yes)
            {
                var (fullSuccess, errorDetail) = await CleanSpecificWebsiteDataAsync(domain);
                HistoryManager.DeleteByDomain(domain);
                UpdateHistoryMasterStatusUI();
                LoadDomainFilters();
                await RefreshHistoryListAsync();

                if (fullSuccess)
                {
                    MessageBox.Show($"Se han eliminado todos los datos de '{domain}'.", "Sitio Limpiado", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(
                        $"Se eliminó el historial de '{domain}', pero la limpieza de sesiones/caché del motor fue parcial:\n{errorDetail}",
                        "Limpieza Parcial",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
        }
    }

    private async void BtnDeleteDay_Click(object sender, RoutedEventArgs e)
    {
        if (LstHistory.SelectedItem is HistoryEntry selected)
        {
            var day = selected.VisitedAt.Date;
            var result = MessageBox.Show(
                $"¿Deseas eliminar todo el historial del día {day:dd/MM/yyyy}?",
                "Eliminar Día",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );
            if (result == MessageBoxResult.Yes)
            {
                HistoryManager.DeleteByDay(day);
                UpdateHistoryMasterStatusUI();
                LoadDomainFilters();
                await RefreshHistoryListAsync();
            }
        }
        else
        {
            MessageBox.Show("Por favor haz clic sobre una entrada en la lista para seleccionar el día que deseas borrar.", "Selección Requerida", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void BtnClearAll_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "¿Estás seguro de que deseas vaciar TODO el historial de navegación?\nEsta acción no se puede deshacer.",
            "Vaciar Historial Completo",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning
        );
        if (result == MessageBoxResult.Yes)
        {
            HistoryManager.ClearAll();
            UpdateHistoryMasterStatusUI();
            LoadDomainFilters();
            await RefreshHistoryListAsync();
        }
    }

    private void BtnOpenClearModal_Click(object sender, RoutedEventArgs e)
    {
        TabClearAdvanced.IsChecked = true;
    }

    // ─────────────────────────────────────────────────────────────────
    // LIMPIEZA AVANZADA DE DATOS (NIVEL SENIOR)
    // ─────────────────────────────────────────────────────────────────
    private void CboClearTimePreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || PanelCustomTime == null || CboClearTimePreset == null) return;
        if (CboClearTimePreset.SelectedItem is ComboBoxItem item)
        {
            bool isCustom = item.Content?.ToString()?.Contains("Tiempo personalizado") ?? false;
            PanelCustomTime.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void BtnCancelAdvanced_Click(object sender, RoutedEventArgs e)
    {
        TabExplore.IsChecked = true;
    }

    private async void BtnExecuteClear_Click(object sender, RoutedEventArgs e)
    {
        DateTime startTime = CalculateCutoffTime();
        DateTime endTime = DateTime.Now;

        bool scopeIsSite = RbScopeSite.IsChecked == true;
        string? targetDomain = scopeIsSite ? (CboScopeTargetDomain.SelectedItem?.ToString() ?? CboScopeTargetDomain.Text)?.Trim() : null;

        if (scopeIsSite && string.IsNullOrWhiteSpace(targetDomain))
        {
            MessageBox.Show("Por favor selecciona o escribe el dominio del sitio web a limpiar.", "Sitio No Especificado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool clearHistory = ChkDataTypeHistory.IsChecked == true;
        bool clearCookies = ChkDataTypeCookies.IsChecked == true;
        bool clearCache = ChkDataTypeCache.IsChecked == true;
        bool clearPasswords = ChkDataTypePasswords.IsChecked == true;
        bool clearLocalStorage = ChkDataTypeLocalStorage.IsChecked == true;

        if (!clearHistory && !clearCookies && !clearCache && !clearPasswords && !clearLocalStorage)
        {
            MessageBox.Show("Selecciona al menos un tipo de dato a eliminar.", "Sin Selección", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        BtnExecuteClear.IsEnabled = false;
        BtnExecuteClear.Content = "⏳ Limpiando datos...";

        try
        {
            // 1. Limpieza en HistoryManager
            if (clearHistory)
            {
                if (startTime == DateTime.MinValue && string.IsNullOrWhiteSpace(targetDomain))
                {
                    HistoryManager.ClearAll();
                }
                else
                {
                    HistoryManager.DeleteByTimeRange(startTime, endTime, targetDomain);
                }
            }

            string? partialWarning = null;

            // 2. Limpieza en CoreWebView2 Profile
            if (_core != null)
            {
                if (!string.IsNullOrWhiteSpace(targetDomain))
                {
                    var (ok, err) = await CleanSpecificWebsiteDataAsync(targetDomain, clearCookies, clearCache, clearLocalStorage);
                    if (!ok) partialWarning = err;
                }
                else
                {
                    var kinds = CoreWebView2BrowsingDataKinds.FileSystems;

                    if (clearCookies) kinds |= CoreWebView2BrowsingDataKinds.Cookies;
                    if (clearCache) kinds |= CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage;
                    if (clearLocalStorage) kinds |= CoreWebView2BrowsingDataKinds.LocalStorage | CoreWebView2BrowsingDataKinds.IndexedDb | CoreWebView2BrowsingDataKinds.WebSql;
                    if (clearPasswords) kinds |= CoreWebView2BrowsingDataKinds.PasswordAutosave | CoreWebView2BrowsingDataKinds.GeneralAutofill;
                    if (clearHistory) kinds |= CoreWebView2BrowsingDataKinds.BrowsingHistory;

                    if (startTime == DateTime.MinValue)
                    {
                        await _core.Profile.ClearBrowsingDataAsync(kinds);
                    }
                    else
                    {
                        await _core.Profile.ClearBrowsingDataAsync(kinds, startTime, endTime);
                    }
                }
            }
            else if (clearCookies || clearCache || clearLocalStorage || clearPasswords)
            {
                partialWarning = "No hay una pestaña activa del motor WebView2 para limpiar cookies o caché en este momento.";
            }

            UpdateHistoryMasterStatusUI();
            LoadDomainFilters();
            await RefreshHistoryListAsync();

            if (!string.IsNullOrEmpty(partialWarning))
            {
                MessageBox.Show(
                    $"Se completó la limpieza del historial, pero con una advertencia:\n{partialWarning}",
                    "Limpieza Parcial",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(
                    scopeIsSite
                        ? $"Se completó la limpieza de '{targetDomain}' exitosamente."
                        : "Se completó la limpieza avanzada de datos de navegación exitosamente.",
                    "Limpieza Completada",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }

            TabExplore.IsChecked = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ocurrió un error durante la limpieza: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnExecuteClear.IsEnabled = true;
            BtnExecuteClear.Content = "🗑️ Ejecutar Limpieza Ahora";
        }
    }

    private DateTime CalculateCutoffTime()
    {
        if (CboClearTimePreset.SelectedItem is ComboBoxItem item)
        {
            string choice = item.Content?.ToString() ?? "";
            DateTime now = DateTime.Now;

            if (choice.Contains("Última hora")) return now.AddHours(-1);
            if (choice.Contains("Últimas 24 horas")) return now.AddDays(-1);
            if (choice.Contains("Últimos 7 días")) return now.AddDays(-7);
            if (choice.Contains("Últimas 4 semanas")) return now.AddDays(-28);
            if (choice.Contains("Desde el principio")) return DateTime.MinValue;

            if (choice.Contains("personalizado"))
            {
                int.TryParse(TxtCustomYears.Text, out int years);
                int.TryParse(TxtCustomMonths.Text, out int months);
                int.TryParse(TxtCustomDays.Text, out int days);
                int.TryParse(TxtCustomHours.Text, out int hours);
                int.TryParse(TxtCustomMinutes.Text, out int minutes);
                int.TryParse(TxtCustomSeconds.Text, out int seconds);

                var cutoff = now
                    .AddYears(-Math.Max(0, years))
                    .AddMonths(-Math.Max(0, months))
                    .AddDays(-Math.Max(0, days))
                    .AddHours(-Math.Max(0, hours))
                    .AddMinutes(-Math.Max(0, minutes))
                    .AddSeconds(-Math.Max(0, seconds));

                return cutoff;
            }
        }

        return DateTime.Now.AddHours(-1);
    }

    private async Task<(bool FullSuccess, string? ErrorMessage)> CleanSpecificWebsiteDataAsync(
        string domain, bool cookies = true, bool cache = true, bool storage = true)
    {
        if (_core == null)
        {
            return (false, "El motor del navegador no está disponible para limpiar cookies o almacenamiento del sitio.");
        }

        var errors = new List<string>();

        try
        {
            string cleanDomain = domain.ToLowerInvariant().Trim();
            if (cleanDomain.StartsWith("www.")) cleanDomain = cleanDomain.Substring(4);

            // 1. Borrar cookies asociadas al dominio (y sus subdominios)
            if (cookies && _core.CookieManager != null)
            {
                try
                {
                    var allCookies = await _core.CookieManager.GetCookiesAsync("");
                    foreach (var c in allCookies)
                    {
                        string cookieDomain = c.Domain.ToLowerInvariant().TrimStart('.');
                        if (cookieDomain.Equals(cleanDomain, StringComparison.OrdinalIgnoreCase) ||
                            cookieDomain.EndsWith("." + cleanDomain, StringComparison.OrdinalIgnoreCase))
                        {
                            _core.CookieManager.DeleteCookie(c);
                        }
                    }
                }
                catch (Exception cookieEx)
                {
                    errors.Add($"Cookies: {cookieEx.Message}");
                }
            }

            // 2. Limpieza aislada por origen (Storage.clearDataForOrigin via CDP)
            if (cache || storage || cookies)
            {
                var storageTypes = new List<string>();
                if (cookies) storageTypes.Add("cookies");
                if (cache)
                {
                    storageTypes.Add("appcache");
                    storageTypes.Add("cache_storage");
                    storageTypes.Add("shader_cache");
                }
                if (storage)
                {
                    storageTypes.Add("file_systems");
                    storageTypes.Add("indexeddb");
                    storageTypes.Add("local_storage");
                    storageTypes.Add("websql");
                    storageTypes.Add("service_workers");
                }

                string typesCsv = string.Join(",", storageTypes);
                string[] origins =
                {
                    $"https://{cleanDomain}",
                    $"https://www.{cleanDomain}",
                    $"http://{cleanDomain}",
                    $"http://www.{cleanDomain}"
                };

                int cdpSuccessCount = 0;
                string? lastCdpError = null;
                foreach (string origin in origins)
                {
                    try
                    {
                        string cdpParams = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            origin,
                            storageTypes = typesCsv
                        });
                        await _core.CallDevToolsProtocolMethodAsync("Storage.clearDataForOrigin", cdpParams);
                        cdpSuccessCount++;
                    }
                    catch (Exception cdpEx)
                    {
                        lastCdpError = cdpEx.Message;
                    }
                }

                if (cdpSuccessCount == 0 && !string.IsNullOrEmpty(lastCdpError))
                {
                    errors.Add($"Almacenamiento de origen: {lastCdpError}");
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }

        return errors.Count == 0
            ? (true, null)
            : (false, string.Join(" | ", errors));
    }

    private void LstHistory_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(LstHistory);
        if (scrollViewer != null)
        {
            GlobalScrollBehavior.HandleSmoothScrollViewerMouseWheel(scrollViewer, e, 28.0);
        }
    }

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        GlobalScrollBehavior.HandleSmoothScrollViewerMouseWheel(sender, e, 28.0);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var descendant = FindVisualChild<T>(child);
            if (descendant != null) return descendant;
        }
        return null;
    }
}
