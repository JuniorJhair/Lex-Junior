using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace LexBrowser.Core.AdBlock.Brave;

public sealed class BraveResourceEntry
{
    public BraveResourceEntry(
        string name,
        IReadOnlyList<string> aliases,
        string mime,
        bool isTemplate,
        IReadOnlyList<string> dependencies,
        int permission,
        byte[] bytes,
        string contentTypeHeader)
    {
        Name = name;
        Aliases = aliases;
        Mime = mime;
        IsTemplate = isTemplate;
        Dependencies = dependencies;
        Permission = permission;
        Bytes = bytes;
        ContentTypeHeader = contentTypeHeader;
    }

    public string Name { get; }
    public IReadOnlyList<string> Aliases { get; }
    public string Mime { get; }
    public bool IsTemplate { get; }
    public IReadOnlyList<string> Dependencies { get; }
    public int Permission { get; }
    public byte[] Bytes { get; }
    public string ContentTypeHeader { get; }

    private string? _utf8Text;
    public string GetUtf8Text() => _utf8Text ??= Encoding.UTF8.GetString(Bytes);
}

/// <summary>
/// Almacén perezoso (bajo demanda) de recursos de redirección y scriptlets
/// extraído de las secciones 20 y 21 de <c>brave-shield.lex</c> (<c>brave-resources.json</c>).
/// Aplica la política de permisos <c>Permission == 0</c> (bloquea <c>trusted-*</c> por defecto).
/// </summary>
public sealed class BraveResourceStore
{
    private readonly Dictionary<string, BraveResourceEntry> _byNameOrAlias;
    private readonly IReadOnlyList<BraveResourceEntry> _entries;

    private BraveResourceStore(
        Dictionary<string, BraveResourceEntry> byNameOrAlias,
        IReadOnlyList<BraveResourceEntry> entries)
    {
        _byNameOrAlias = byNameOrAlias;
        _entries = entries;
    }

    public int Count => _entries.Count;

    internal IReadOnlyList<BraveResourceEntry> Entries => _entries;

    public static BraveResourceStore Empty { get; } =
        new(new Dictionary<string, BraveResourceEntry>(StringComparer.OrdinalIgnoreCase), Array.Empty<BraveResourceEntry>());

    internal static BraveResourceStore LoadFromSections(byte[] indexJsonUtf8, byte[] blob)
    {
        if (indexJsonUtf8 == null || indexJsonUtf8.Length == 0)
            return Empty;

        using var doc = JsonDocument.Parse(indexJsonUtf8);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return Empty;

        var list = new List<BraveResourceEntry>(doc.RootElement.GetArrayLength());
        var map = new Dictionary<string, BraveResourceEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string name = el.GetProperty("n").GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(name))
                continue;

            string mime = el.GetProperty("m").GetString() ?? "application/octet-stream";
            bool isTemplate = el.TryGetProperty("tpl", out var tplEl) && tplEl.GetBoolean();
            int perm = el.TryGetProperty("p", out var pEl) ? pEl.GetInt32() : 0;
            int off = el.GetProperty("o").GetInt32();
            int len = el.GetProperty("l").GetInt32();

            var aliases = ReadStringList(el, "a");
            var deps = ReadStringList(el, "d");

            byte[] data = Array.Empty<byte>();
            if (len > 0 && off >= 0 && off + len <= blob.Length)
            {
                data = new byte[len];
                Buffer.BlockCopy(blob, off, data, 0, len);
            }

            string header = BuildContentTypeHeader(mime);
            var entry = new BraveResourceEntry(name, aliases, mime, isTemplate, deps, perm, data, header);
            list.Add(entry);

            map.TryAdd(name, entry);
            foreach (var alias in aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias))
                    map.TryAdd(alias, entry);
            }
        }

        return new BraveResourceStore(map, list);
    }

    public bool TryGetResource(string? nameOrAlias, out BraveResourceEntry? entry, bool allowPrivileged = false)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(nameOrAlias))
            return false;

        string clean = StripPrioritySuffix(nameOrAlias.Trim());
        if (_byNameOrAlias.TryGetValue(clean, out var found))
        {
            if (found.Permission != 0 && !allowPrivileged)
                return false;
            entry = found;
            return true;
        }
        return false;
    }

    public bool TryGetRedirectResource(string? nameOrAlias, out BraveResourceEntry? entry, bool allowPrivileged = false)
    {
        if (TryGetResource(nameOrAlias, out var found, allowPrivileged) &&
            found != null &&
            !found.IsTemplate)
        {
            entry = found;
            return true;
        }

        entry = null;
        return false;
    }

    public string ResolveCanonicalName(string? nameOrAlias)
    {
        if (string.IsNullOrWhiteSpace(nameOrAlias))
            return string.Empty;

        string clean = StripPrioritySuffix(nameOrAlias.Trim());
        return _byNameOrAlias.TryGetValue(clean, out var found) ? found.Name : clean;
    }

    public static bool IsImaResource(string? nameOrCanonical)
        => !string.IsNullOrEmpty(nameOrCanonical) &&
           (nameOrCanonical.Contains("google-ima", StringComparison.OrdinalIgnoreCase) ||
            nameOrCanonical.EndsWith("ima3.js", StringComparison.OrdinalIgnoreCase));

    public static bool IsVastOrVmapResource(string? nameOrCanonical)
        => !string.IsNullOrEmpty(nameOrCanonical) &&
           (nameOrCanonical.Contains("vast", StringComparison.OrdinalIgnoreCase) ||
            nameOrCanonical.Contains("vmap", StringComparison.OrdinalIgnoreCase));

    private static string StripPrioritySuffix(string value)
    {
        int colon = value.LastIndexOf(':');
        if (colon > 0 && colon < value.Length - 1 && int.TryParse(value.AsSpan(colon + 1), out _))
            return value.Substring(0, colon);
        return value;
    }

    private static IReadOnlyList<string> ReadStringList(JsonElement parent, string prop)
    {
        if (!parent.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return Array.Empty<string>();

        var result = new string[arr.GetArrayLength()];
        int i = 0;
        foreach (var item in arr.EnumerateArray())
            result[i++] = item.GetString() ?? string.Empty;
        return result;
    }

    private static string BuildContentTypeHeader(string mime)
    {
        if (string.IsNullOrWhiteSpace(mime) || mime == "template")
            return "Content-Type: application/javascript; charset=utf-8";

        bool needsCharset =
            mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            mime.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
            mime.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            mime.Contains("xml", StringComparison.OrdinalIgnoreCase);

        return needsCharset && !mime.Contains("charset=", StringComparison.OrdinalIgnoreCase)
            ? $"Content-Type: {mime}; charset=utf-8"
            : $"Content-Type: {mime}";
    }
}
