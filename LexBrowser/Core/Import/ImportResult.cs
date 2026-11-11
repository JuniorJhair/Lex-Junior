using System.Collections.Generic;

namespace LexBrowser.Core.Import;

/// <summary>
/// Resultado por categoría (marcadores, historial o contraseñas) sin exponer secretos.
/// </summary>
public sealed record ImportCategoryResult(
    string Category,
    int Imported,
    int Duplicates,
    int Skipped,
    int UnsupportedAppBoundCount = 0,
    string? Error = null,
    string? Warning = null)
{
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool HasWarning => !string.IsNullOrWhiteSpace(Warning);
}

/// <summary>
/// Resumen completo de una operación de importación.
/// </summary>
public sealed class ImportSummary
{
    public string SourceDescription { get; init; } = string.Empty;
    public ImportCategoryResult? Bookmarks { get; set; }
    public ImportCategoryResult? History { get; set; }
    public ImportCategoryResult? Passwords { get; set; }
    public string? CsvRecommendation { get; set; }
    public List<string> Notes { get; } = new();
}

