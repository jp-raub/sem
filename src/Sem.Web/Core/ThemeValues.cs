using System.Collections.Frozen;

namespace Sem.Web.Core;

/// <summary>A validated, canonical six-digit CSS hexadecimal color.</summary>
public sealed record HexColor
{
    private HexColor(string value) => Value = value;

    /// <summary>The value in canonical uppercase <c>#RRGGBB</c> form.</summary>
    public string Value { get; }

    public static HexColor Parse(string value)
    {
        if (!TryParse(value, out var color))
            throw new ArgumentException("Color must be exactly #RRGGBB using hexadecimal digits.", nameof(value));
        return color!;
    }

    public static bool TryParse(string? value, out HexColor? color)
    {
        color = null;
        if (value is null || value.Length != 7 || value[0] != '#') return false;
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f') ||
                  (character >= 'A' && character <= 'F'))) return false;
        }

        color = new HexColor("#" + value[1..].ToUpperInvariant());
        return true;
    }

    public override string ToString() => Value;
}

/// <summary>One immutable catalog entry. IDs are lowercase ASCII kebab-case.</summary>
public sealed class ThemeDefinition
{
    public ThemeDefinition(string id, string name, HexColor accent, HexColor background, HexColor surface, HexColor text)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(accent);
        ArgumentNullException.ThrowIfNull(background);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(text);

        if (!ThemeCatalog.IsValidId(id))
            throw new ArgumentException("Theme IDs must be 1-64 lowercase ASCII letters/digits separated by single hyphens.", nameof(id));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 30 || name.Any(char.IsControl))
            throw new ArgumentException("Theme names must be nonblank, contain no control characters, and be at most 30 characters.", nameof(name));

        Id = id;
        Name = name;
        Accent = accent;
        Background = background;
        Surface = surface;
        Text = text;
    }

    public string Id { get; }
    public string Name { get; }
    public HexColor Accent { get; }
    public HexColor Background { get; }
    public HexColor Surface { get; }
    public HexColor Text { get; }
}

/// <summary>An immutable, case-sensitive theme catalog. The default catalog is built-in and read-only.</summary>
public sealed class ThemeCatalog
{
    private readonly FrozenDictionary<string, ThemeDefinition> _byId;
    private readonly IReadOnlyList<ThemeDefinition> _themes;

    private static readonly ThemeCatalog BuiltInCatalog = new(new[]
    {
        Create("green", "Forest", "#1e6557", "#f5f7f3", "#fffefa", "#18312e"),
        Create("blue", "Ocean", "#285f91", "#f5f7f3", "#fffefa", "#18312e"),
        Create("purple", "Plum", "#775087", "#f5f7f3", "#fffefa", "#18312e"),
        Create("amber", "Amber", "#906322", "#f5f7f3", "#fffefa", "#18312e"),
        Create("rose", "Rose", "#a14365", "#fbf3f6", "#fffafb", "#36212a"),
        Create("teal", "Teal", "#14766e", "#eff8f7", "#faffff", "#183a37"),
        Create("slate", "Slate", "#526577", "#f1f4f7", "#ffffff", "#263440"),
        Create("navy", "Midnight", "#31547b", "#edf2f8", "#f8fbff", "#1e3047"),
        Create("coral", "Coral", "#aa513f", "#fff3ed", "#fffaf6", "#482d25"),
        Create("mono", "Monochrome", "#555555", "#f5f5f5", "#ffffff", "#252525")
    });

    public ThemeCatalog(IEnumerable<ThemeDefinition> themes)
    {
        ArgumentNullException.ThrowIfNull(themes);
        var copy = new List<ThemeDefinition>();
        var byId = new Dictionary<string, ThemeDefinition>(StringComparer.Ordinal);
        foreach (var theme in themes)
        {
            if (theme is null) throw new ArgumentException("Catalog entries cannot be null.", nameof(themes));
            if (!byId.TryAdd(theme.Id, theme))
                throw new ArgumentException($"Duplicate theme ID '{theme.Id}'.", nameof(themes));
            copy.Add(theme);
        }

        if (copy.Count == 0) throw new ArgumentException("A theme catalog must contain at least one theme.", nameof(themes));
        _themes = Array.AsReadOnly(copy.ToArray());
        _byId = byId.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>The immutable built-in ten-theme catalog.</summary>
    public static ThemeCatalog Default => BuiltInCatalog;

    /// <summary>A stable read-only snapshot in catalog order.</summary>
    public IReadOnlyList<ThemeDefinition> Themes => _themes;

    public bool TryGet(string id, out ThemeDefinition? theme)
    {
        ArgumentNullException.ThrowIfNull(id);
        var found = _byId.TryGetValue(id, out var value);
        theme = value;
        return found;
    }

    public ThemeDefinition GetRequired(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _byId.TryGetValue(id, out var theme)
            ? theme
            : throw new KeyNotFoundException($"Theme ID '{id}' is not present in this catalog.");
    }

    internal static bool IsValidId(string id)
    {
        if (id.Length is < 1 or > 64 || !IsLowerOrDigit(id[0]) || !IsLowerOrDigit(id[^1])) return false;
        var previousWasHyphen = false;
        foreach (var character in id)
        {
            if (character == '-')
            {
                if (previousWasHyphen) return false;
                previousWasHyphen = true;
            }
            else
            {
                if (!IsLowerOrDigit(character)) return false;
                previousWasHyphen = false;
            }
        }
        return true;
    }

    private static bool IsLowerOrDigit(char value) =>
        (value >= 'a' && value <= 'z') || (value >= '0' && value <= '9');

    private static ThemeDefinition Create(string id, string name, string accent, string background, string surface, string text) =>
        new(id, name, HexColor.Parse(accent), HexColor.Parse(background), HexColor.Parse(surface), HexColor.Parse(text));
}

/// <summary>Light/dark appearance preference. System is retained as a value, never resolved by this library.</summary>
public enum ThemeMode
{
    System,
    Light,
    Dark
}

/// <summary>One optional selection layer; a selected catalog theme supplies all four colors.</summary>
public sealed class ThemeLayer
{
    public ThemeLayer(string? themeId = null, ThemeMode? mode = null)
    {
        if (themeId is not null && !ThemeCatalog.IsValidId(themeId))
            throw new ArgumentException("Theme IDs must be 1-64 lowercase ASCII letters/digits separated by single hyphens.", nameof(themeId));
        if (mode.HasValue && !Enum.IsDefined(typeof(ThemeMode), mode.Value))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown theme mode.");

        ThemeId = themeId;
        Mode = mode;
    }

    public string? ThemeId { get; }
    public ThemeMode? Mode { get; }
}

/// <summary>A fully resolved palette plus the independently inherited appearance mode.</summary>
public sealed class ResolvedTheme
{
    internal ResolvedTheme(HexColor accent, HexColor background, HexColor surface, HexColor text, ThemeMode mode)
    {
        Accent = accent;
        Background = background;
        Surface = surface;
        Text = text;
        Mode = mode;
    }

    public HexColor Accent { get; }
    public HexColor Background { get; }
    public HexColor Surface { get; }
    public HexColor Text { get; }
    public ThemeMode Mode { get; }
}

/// <summary>Thrown when an explicit layer reference or the required fallback Forest ID is absent from the injected catalog.</summary>
public sealed class ThemeReferenceException : ArgumentException
{
    public ThemeReferenceException(string layerName, string themeId)
        : base($"The {layerName} theme reference '{themeId}' is not present in the supplied catalog.")
    {
        LayerName = layerName;
        ThemeId = themeId;
    }

    public string LayerName { get; }
    public string ThemeId { get; }
}

/// <summary>Pure deterministic resolver for personal → event → site → Forest/System inheritance.</summary>
public static class ThemeResolver
{
    private readonly record struct LayerSource(string Name, ThemeLayer? Layer);

    public static ResolvedTheme Resolve(
        ThemeCatalog catalog,
        ThemeLayer? personal = null,
        ThemeLayer? eventLayer = null,
        ThemeLayer? site = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var layers = new[]
        {
            new LayerSource("personal", personal),
            new LayerSource("event", eventLayer),
            new LayerSource("site", site)
        };

        // Fail closed for every explicit reference, including a shadowed lower-priority reference.
        foreach (var (name, layer) in layers)
        {
            if (layer?.ThemeId is { } themeId && !catalog.TryGet(themeId, out _))
                throw new ThemeReferenceException(name, themeId);
        }

        var selectedThemeId = layers.Select(source => source.Layer?.ThemeId).FirstOrDefault(id => id is not null);
        var resolvedId = selectedThemeId ?? "green";
        if (!catalog.TryGet(resolvedId, out var theme))
            throw new ThemeReferenceException("fallback", resolvedId);
        var mode = layers.Select(source => source.Layer?.Mode).FirstOrDefault(value => value.HasValue) ?? ThemeMode.System;
        return new ResolvedTheme(theme!.Accent, theme.Background, theme.Surface, theme.Text, mode);
    }
}
