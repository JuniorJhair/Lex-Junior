using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Media;

namespace LexBrowser.Core;

public class HistoryEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public DateTime VisitedAt { get; set; } = DateTime.Now;

    // Propiedades calculadas solo para la interfaz gráfica (no se guardan en disco)
    [JsonIgnore] public string DisplayTime => VisitedAt.ToString("HH:mm:ss");
    [JsonIgnore] public string DisplayDate => VisitedAt.ToString("dd/MM/yyyy");
    [JsonIgnore]
    public string DayGroup
    {
        get
        {
            var date = VisitedAt.Date;
            if (date == DateTime.Today) return "Hoy";
            if (date == DateTime.Today.AddDays(-1)) return "Ayer";
            return VisitedAt.ToString("dddd, d 'de' MMMM 'de' yyyy");
        }
    }

    private ImageSource? _faviconImage;
    private bool _isResolvingFavicon;

    [JsonIgnore]
    public ImageSource? FaviconImage
    {
        get
        {
            if (_faviconImage != null) return _faviconImage;

            // Consultar caché síncrona en memoria primero
            var cached = Engine.FaviconService.GetCachedFavicon(Domain);
            if (cached != null)
            {
                _faviconImage = cached;
                return cached;
            }

            if (!_isResolvingFavicon && !string.IsNullOrWhiteSpace(Url))
            {
                _isResolvingFavicon = true;
                _ = ResolveFaviconAsync();
            }
            return null;
        }
    }

    [JsonIgnore] public bool HasFaviconImage => FaviconImage != null;
    [JsonIgnore] public bool HasNoFaviconImage => FaviconImage == null;

    private async Task ResolveFaviconAsync()
    {
        try
        {
            var img = await Engine.FaviconService.ResolveFaviconAsync(Url);
            if (img != null)
            {
                _faviconImage = img;
                System.Windows.Application.Current?.Dispatcher?.BeginInvoke(() =>
                {
                    OnPropertyChanged(nameof(FaviconImage));
                    OnPropertyChanged(nameof(HasFaviconImage));
                    OnPropertyChanged(nameof(HasNoFaviconImage));
                });
            }
        }
        catch { }
    }

    [JsonIgnore] public string FaviconUrl => "pack://application:,,,/Assets/logo.png";
}

public class DomainVisitStat
{
    public string Domain { get; set; } = string.Empty;
    public int Count { get; set; }
    public string DisplayText => $"{Domain} ({Count})";
}

/// <summary>
/// Fachada estática sobre <see cref="HistoryStore"/> para la aplicación.
/// Toda la lógica vive en HistoryStore (que sí es testeable).
/// </summary>
public static class HistoryManager
{
    private static readonly string HistoryFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LexBrowser",
        "history.json"
    );

    private static readonly HistoryStore _store =
        new(HistoryFilePath, () => SettingsManager.Current.EnableHistory);

    public static event Action? HistoryChanged
    {
        add => _store.HistoryChanged += value;
        remove => _store.HistoryChanged -= value;
    }

    public static void Load() => _store.Load();
    public static void Save() => _store.Flush();

    /// <summary>Fuerza la escritura de cambios pendientes (llamar al cerrar la aplicación).</summary>
    public static void Flush() => _store.Flush();

    public static string ExtractRootDomain(string url) => HistoryStore.ExtractRootDomain(url);

    public static void AddEntry(string url, string title) => _store.AddEntry(url, title);
    public static void UpdateTitle(string url, string title) => _store.UpdateTitle(url, title);

    public static List<HistoryEntry> GetEntries(string? filterText = null, string? domain = null, DateTime? fromDate = null, DateTime? toDate = null)
        => _store.GetEntries(filterText, domain, fromDate, toDate);

    public static List<DomainVisitStat> GetDistinctDomains() => _store.GetDistinctDomains();

    public static void DeleteEntry(string id) => _store.DeleteEntry(id);
    public static void DeleteEntries(IEnumerable<string> ids) => _store.DeleteEntries(ids);
    public static void DeleteByDay(DateTime day) => _store.DeleteByDay(day);
    public static void DeleteByDomain(string domain, DateTime? fromTime = null) => _store.DeleteByDomain(domain, fromTime);
    public static void DeleteByTimeRange(DateTime fromTime, DateTime toTime, string? domain = null) => _store.DeleteByTimeRange(fromTime, toTime, domain);
    public static void ClearAll() => _store.ClearAll();

    public static string GetHistoryFileSizeFormatted() => _store.GetFileSizeFormatted();
    public static int GetTotalCount() => _store.GetTotalCount();
}
