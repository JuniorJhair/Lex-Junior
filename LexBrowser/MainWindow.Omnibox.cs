using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LexBrowser.Core;
using LexBrowser.Core.Engine;
using LexBrowser.Core.Pdf;
using LexBrowser.UI.Models;

namespace LexBrowser;

public partial class MainWindow
{
    private bool _isUpdatingOmniboxTextProgrammatically = false;
    private bool _suppressInlineAutocomplete = false;
    private int _omniboxTextChangedDepth = 0;
    private readonly ObservableCollection<OmniboxSuggestionItem> _omniboxSuggestions = new();
    private int _selectedOmniboxIndex = 0;
    private CancellationTokenSource? _omniboxLiveSuggestCts;

    private static readonly SolidColorBrush BrushHttpsSecure = CreateFrozenBrush(0x10, 0xB9, 0x81);
    private static readonly SolidColorBrush BrushHttpWarning = CreateFrozenBrush(0xF5, 0x9E, 0x0B);

    private void TxtUrl_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!TxtUrl.IsKeyboardFocusWithin)
        {
            TxtUrl.Focus();
            TxtUrl.SelectAll();
            e.Handled = true;
        }
    }

    private void TxtUrl_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        UpdateSecurityIndicatorUI();
        string current = TxtUrl.Text.Trim();
        if (!string.IsNullOrEmpty(current) &&
            !current.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !current.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            bool isPrivate = _isGuestSession || (_activeTab?.IsIncognito == true);
            var localHistory = isPrivate ? Array.Empty<HistoryEntry>() : HistoryManager.GetRecentSnapshot(2500);
            var localBookmarks = isPrivate ? (IReadOnlyList<BookmarkItem>)Array.Empty<BookmarkItem>() : BookmarkManager.Bookmarks.ToArray();
            _ = RefreshOmniboxSuggestionsAsync(current, localHistory, localBookmarks, skipInitialDebounce: true);
        }
    }

    private void TxtUrl_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _omniboxLiveSuggestCts?.Cancel();
        PopupOmniboxSuggestions.IsOpen = false;
        UpdateSecurityIndicatorUI();
    }

    private void TxtUrl_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingOmniboxTextProgrammatically || !TxtUrl.IsKeyboardFocusWithin || _omniboxTextChangedDepth > 0)
            return;

        _omniboxTextChangedDepth++;
        try
        {
            string rawText = TxtUrl.Text;
            string query = rawText.Trim();
            if (string.IsNullOrEmpty(query))
            {
                _omniboxLiveSuggestCts?.Cancel();
                PopupOmniboxSuggestions.IsOpen = false;
                return;
            }

            bool isPrivate = _isGuestSession || (_activeTab?.IsIncognito == true);
            var localHistory = isPrivate
                ? Array.Empty<HistoryEntry>()
                : HistoryManager.GetRecentSnapshot(2500);
            var localBookmarks = isPrivate
                ? (IReadOnlyList<BookmarkItem>)Array.Empty<BookmarkItem>()
                : BookmarkManager.Bookmarks.ToArray();

            // Verificar que el cambio sea estrictamente una inserción al final del texto antes de autocompletar en línea.
            // Esto evita inversiones o transposiciones de caracteres al editar en medio o escribir rápidamente.
            bool isForwardAppendAtEnd = false;
            if (e.Changes.Count == 1)
            {
                var change = e.Changes.First();
                isForwardAppendAtEnd = change.AddedLength > 0 &&
                                       change.RemovedLength == 0 &&
                                       (change.Offset + change.AddedLength == rawText.Length);
            }

            if (!_suppressInlineAutocomplete && isForwardAppendAtEnd && TxtUrl.SelectionLength == 0 && !rawText.EndsWith(' '))
            {
                string? match = OmniboxSuggestionService.GetInlineAutocompleteMatch(rawText, localHistory, localBookmarks);
                if (!string.IsNullOrEmpty(match) &&
                    match.StartsWith(rawText, StringComparison.OrdinalIgnoreCase) &&
                    match.Length > rawText.Length)
                {
                    int userLen = rawText.Length;
                    string predicted = rawText + match.Substring(userLen);

                    _isUpdatingOmniboxTextProgrammatically = true;
                    TxtUrl.Text = predicted;
                    TxtUrl.SelectionStart = userLen;
                    TxtUrl.SelectionLength = predicted.Length - userLen;
                    _isUpdatingOmniboxTextProgrammatically = false;
                }
            }
            _suppressInlineAutocomplete = false;

            _ = RefreshOmniboxSuggestionsAsync(query, localHistory, localBookmarks, skipInitialDebounce: false);
        }
        finally
        {
            _omniboxTextChangedDepth--;
        }
    }

    private async Task RefreshOmniboxSuggestionsAsync(
        string query,
        IReadOnlyList<HistoryEntry>? localHistory = null,
        IReadOnlyList<BookmarkItem>? localBookmarks = null,
        bool skipInitialDebounce = false)
    {
        _omniboxLiveSuggestCts?.Cancel();
        var cts = new CancellationTokenSource();
        _omniboxLiveSuggestCts = cts;
        var token = cts.Token;

        // Debounce inicial de 60 ms para escritura continua: si el usuario escribe rápido, no saturamos la UI
        if (!skipInitialDebounce)
        {
            try
            {
                await Task.Delay(60, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (token.IsCancellationRequested || !TxtUrl.IsKeyboardFocusWithin) return;

        string engineName = SettingsManager.Current.DefaultSearchEngine;
        bool isPrivate = _isGuestSession || (_activeTab?.IsIncognito == true);

        localHistory ??= isPrivate
            ? Array.Empty<HistoryEntry>()
            : HistoryManager.GetRecentSnapshot(2500);
        localBookmarks ??= isPrivate
            ? (IReadOnlyList<BookmarkItem>)Array.Empty<BookmarkItem>()
            : BookmarkManager.Bookmarks.ToArray();

        try
        {
            // 1. Construir sugerencias locales fuera del hilo UI usando los campos precalculados de HistoryEntry
            var initialList = await Task.Run(() => OmniboxSuggestionService.BuildSuggestions(
                query,
                localHistory,
                localBookmarks,
                liveGoogleSuggestions: null,
                searchEngineName: engineName,
                cancellationToken: token), token);

            if (token.IsCancellationRequested || !TxtUrl.IsKeyboardFocusWithin) return;
            ApplyOmniboxSuggestionsToPopup(initialList);

            // 2. Debounce de 100 ms y consulta al motor de búsqueda configurado (respetando privacidad e incógnito)
            if (isPrivate || !SettingsManager.Current.EnableSearchSuggestions)
                return;

            await Task.Delay(100, token);
            if (token.IsCancellationRequested || !TxtUrl.IsKeyboardFocusWithin) return;

            var liveSuggestions = await OmniboxSuggestionService.FetchSearchSuggestionsAsync(query, engineName, token);
            if (token.IsCancellationRequested || !TxtUrl.IsKeyboardFocusWithin || liveSuggestions.Count == 0)
                return;

            var combinedList = await Task.Run(() => OmniboxSuggestionService.BuildSuggestions(
                query,
                localHistory,
                localBookmarks,
                liveGoogleSuggestions: liveSuggestions,
                searchEngineName: engineName,
                cancellationToken: token), token);

            if (token.IsCancellationRequested || !TxtUrl.IsKeyboardFocusWithin) return;
            ApplyOmniboxSuggestionsToPopup(combinedList);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void ApplyOmniboxSuggestionsToPopup(List<OmniboxSuggestionItem> items)
    {
        if (items == null || items.Count == 0)
        {
            _omniboxSuggestions.Clear();
            PopupOmniboxSuggestions.IsOpen = false;
            return;
        }

        // Actualización in-situ para reutilizar los contenedores visuales de WPF y evitar destrucción masiva del árbol visual
        int targetCount = items.Count;
        for (int i = 0; i < targetCount; i++)
        {
            items[i].IsSelected = (i == 0);
            if (i < _omniboxSuggestions.Count)
            {
                _omniboxSuggestions[i] = items[i];
            }
            else
            {
                _omniboxSuggestions.Add(items[i]);
            }
        }

        while (_omniboxSuggestions.Count > targetCount)
        {
            _omniboxSuggestions.RemoveAt(_omniboxSuggestions.Count - 1);
        }

        _selectedOmniboxIndex = 0;
        PopupOmniboxSuggestions.IsOpen = _omniboxSuggestions.Count > 0 && TxtUrl.IsKeyboardFocusWithin;
    }

    private void TxtUrl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back || e.Key == Key.Delete)
        {
            _suppressInlineAutocomplete = true;

            if (TxtUrl.SelectionLength > 0 && TxtUrl.SelectionStart + TxtUrl.SelectionLength == TxtUrl.Text.Length)
            {
                e.Handled = true;
                int start = TxtUrl.SelectionStart;
                _isUpdatingOmniboxTextProgrammatically = true;
                TxtUrl.Text = TxtUrl.Text.Substring(0, start);
                TxtUrl.SelectionStart = start;
                TxtUrl.SelectionLength = 0;
                _isUpdatingOmniboxTextProgrammatically = false;
                _ = RefreshOmniboxSuggestionsAsync(TxtUrl.Text.Trim());
                return;
            }
        }
        else if (e.Key == Key.Right || e.Key == Key.Tab || e.Key == Key.End)
        {
            if (TxtUrl.SelectionLength > 0 && TxtUrl.SelectionStart + TxtUrl.SelectionLength == TxtUrl.Text.Length)
            {
                e.Handled = true;
                TxtUrl.SelectionStart = TxtUrl.Text.Length;
                TxtUrl.SelectionLength = 0;
                return;
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (TxtUrl.SelectionLength > 0 && TxtUrl.SelectionStart + TxtUrl.SelectionLength == TxtUrl.Text.Length)
            {
                e.Handled = true;
                int start = TxtUrl.SelectionStart;
                _isUpdatingOmniboxTextProgrammatically = true;
                TxtUrl.Text = TxtUrl.Text.Substring(0, start);
                TxtUrl.SelectionStart = start;
                TxtUrl.SelectionLength = 0;
                _isUpdatingOmniboxTextProgrammatically = false;
                PopupOmniboxSuggestions.IsOpen = false;
                return;
            }
            if (PopupOmniboxSuggestions.IsOpen)
            {
                e.Handled = true;
                PopupOmniboxSuggestions.IsOpen = false;
                return;
            }
        }
        else
        {
            _suppressInlineAutocomplete = false;
        }

        if (!PopupOmniboxSuggestions.IsOpen || _omniboxSuggestions.Count == 0)
        {
            return;
        }

        if (e.Key == Key.Down)
        {
            e.Handled = true;
            MoveOmniboxSelection(+1);
        }
        else if (e.Key == Key.Up)
        {
            e.Handled = true;
            MoveOmniboxSelection(-1);
        }
    }

    private void MoveOmniboxSelection(int delta)
    {
        if (_omniboxSuggestions.Count == 0) return;

        _selectedOmniboxIndex = (_selectedOmniboxIndex + delta + _omniboxSuggestions.Count) % _omniboxSuggestions.Count;
        for (int i = 0; i < _omniboxSuggestions.Count; i++)
        {
            _omniboxSuggestions[i].IsSelected = (i == _selectedOmniboxIndex);
        }

        var selected = _omniboxSuggestions[_selectedOmniboxIndex];
        _isUpdatingOmniboxTextProgrammatically = true;
        TxtUrl.Text = selected.TargetInputOrUrl;
        TxtUrl.CaretIndex = TxtUrl.Text.Length;
        _isUpdatingOmniboxTextProgrammatically = false;
    }

    private void OmniboxSuggestionRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Si el clic se originó sobre el botón ✕ de eliminar sugerencia, no navegar
        if (e.OriginalSource is DependencyObject dep && FindVisualParent<Button>(dep) != null)
        {
            return;
        }

        if (sender is FrameworkElement fe && fe.DataContext is OmniboxSuggestionItem item)
        {
            e.Handled = true;
            PopupOmniboxSuggestions.IsOpen = false;
            NavigateOmniboxInput(item.TargetInputOrUrl);
        }
    }

    private void BtnDeleteOmniboxSuggestion_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button btn && btn.Tag is OmniboxSuggestionItem item && !string.IsNullOrEmpty(item.HistoryEntryId))
        {
            e.Handled = true;
            HistoryManager.DeleteEntry(item.HistoryEntryId);
            _omniboxSuggestions.Remove(item);
            if (_omniboxSuggestions.Count == 0)
            {
                PopupOmniboxSuggestions.IsOpen = false;
            }
        }
    }

    private void TxtUrl_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _activeTab?.WebView == null) return;

        e.Handled = true;
        _omniboxLiveSuggestCts?.Cancel();

        string input = TxtUrl.Text.Trim();
        if (PopupOmniboxSuggestions.IsOpen &&
            _selectedOmniboxIndex > 0 &&
            _selectedOmniboxIndex < _omniboxSuggestions.Count)
        {
            input = _omniboxSuggestions[_selectedOmniboxIndex].TargetInputOrUrl.Trim();
        }

        PopupOmniboxSuggestions.IsOpen = false;
        NavigateOmniboxInput(input);
    }

    private void NavigateOmniboxInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || _activeTab?.WebView == null) return;

        string targetUrl = OmniboxInputResolver.ResolveNavigationTarget(input);
        if (string.IsNullOrWhiteSpace(targetUrl)) return;

        _isUpdatingOmniboxTextProgrammatically = true;
        TxtUrl.Text = targetUrl;
        _isUpdatingOmniboxTextProgrammatically = false;

        string finalNavUrl = PdfStudioService.IsPdfTarget(targetUrl)
            ? PdfStudioService.GetViewerUrl(targetUrl)
            : targetUrl;

        if (_activeTab.WebView.CoreWebView2 != null)
        {
            ResumeTab(_activeTab);
            _activeTab.WebView.CoreWebView2.Navigate(finalNavUrl);
            _activeTab.WebView.Focus();
        }
        else if (Uri.TryCreate(finalNavUrl, UriKind.Absolute, out var navUri))
        {
            _activeTab.WebView.Source = navUri;
        }
    }

    private void UpdateSecurityIndicatorUI()
    {
        if (TxtSecurityIcon == null || BadgeGoogleOmnibox == null) return;
        string url = _activeTab?.Url ?? "";
        bool isTypingOrNewTab = _activeTab == null || _activeTab.IsNewTab || string.IsNullOrWhiteSpace(url) || (TxtUrl != null && TxtUrl.IsKeyboardFocusWithin);

        if (isTypingOrNewTab)
        {
            BadgeGoogleOmnibox.Visibility = Visibility.Visible;
            TxtSecurityIcon.Visibility = Visibility.Collapsed;
        }
        else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            BadgeGoogleOmnibox.Visibility = Visibility.Collapsed;
            TxtSecurityIcon.Visibility = Visibility.Visible;
            TxtSecurityIcon.Text = "🔒";
            TxtSecurityIcon.Foreground = BrushHttpsSecure;
            TxtSecurityIcon.ToolTip = "Conexión cifrada segura (HTTPS)";
        }
        else
        {
            BadgeGoogleOmnibox.Visibility = Visibility.Collapsed;
            TxtSecurityIcon.Visibility = Visibility.Visible;
            TxtSecurityIcon.Text = "⚠️";
            TxtSecurityIcon.Foreground = BrushHttpWarning;
            TxtSecurityIcon.ToolTip = "Conexión no cifrada (HTTP)";
        }
    }

    private void UpdateNavigationButtons()
    {
        if (_activeTab?.WebView?.CoreWebView2 != null)
        {
            BtnBack.IsEnabled = _activeTab.WebView.CoreWebView2.CanGoBack;
            BtnForward.IsEnabled = _activeTab.WebView.CoreWebView2.CanGoForward;
        }
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab?.WebView?.CoreWebView2?.CanGoBack == true)
            _activeTab.WebView.CoreWebView2.GoBack();
    }

    private void BtnForward_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab?.WebView?.CoreWebView2?.CanGoForward == true)
            _activeTab.WebView.CoreWebView2.GoForward();
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab != null)
        {
            RecordUserReloadAndCheckCompat(_activeTab);
            _activeTab.WebView?.CoreWebView2?.Reload();
        }
    }

    private void BtnHome_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab?.WebView?.CoreWebView2 != null)
        {
            _activeTab.IsNewTab = true;
            _activeTab.Url = "";
            _activeTab.Title = _activeTab.IsGuest ? "Modo Invitado" : (_activeTab.IsIncognito ? "Nueva pestaña incógnito" : "Nueva pestaña");
            _isUpdatingOmniboxTextProgrammatically = true;
            TxtUrl.Text = "";
            _isUpdatingOmniboxTextProgrammatically = false;
            _activeTab.WebView.NavigateToString(NewTabPage.GetHtml(_activeTab.IsIncognito, isGuest: _activeTab.IsGuest));
            UpdateNavigationButtons();
        }
    }
}
