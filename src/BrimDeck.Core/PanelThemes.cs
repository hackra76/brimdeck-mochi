using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

public enum PanelThemeKind { Basic, Extension, Custom }

// One color the theme sets. Minimum is the contrast it must keep against the backgrounds Against names; 0 means not checked.
public sealed record PanelThemeToken(string Key, string Chinese, string English, double Minimum = 0, PanelThemeBackdrop Against = PanelThemeBackdrop.None);
public enum PanelThemeBackdrop { None, Surface, SurfaceAndPopup, Accent, Indicator }

public sealed record PanelThemeInfo(string Id, PanelThemeKind Kind, PanelThemeFile File, string? Path, string? Error)
{
    public bool BuiltIn => Path is null;
    public bool Usable => Error is null;
    public string DisplayName => Loc.IsEnglish && File.NameEn.Length > 0 ? File.NameEn : File.Name.Length > 0 ? File.Name : Id;
    public string DisplayDescription => Loc.IsEnglish && File.DescriptionEn.Length > 0 ? File.DescriptionEn : File.Description;
}

[JsonSerializable(typeof(PanelThemeFile))]
[JsonSerializable(typeof(PanelThemeDecor))]
[JsonSerializable(typeof(string))]
internal sealed partial class PanelThemeJson : JsonSerializerContext;

// The panel's color theme: the collapsed notch, capsule and indicator, the quota notice and the expanded panel with its pop-ups.
// A theme is a JSON file in the format of PanelThemeFile. The built-in themes are such files embedded in this assembly
// (Themes/*.json); themes the user adds are files in the data folder's "themes" folder (PanelThemes.Files.cs). Every theme names
// a base theme and sets only what it changes, so a file of a few colors is a complete theme. Readability does not depend on the
// theme: a resolved palette is held to the contrast minimums of Tokens, and any color that misses one is moved, along its own
// hue, just far enough to reach it (PanelThemes.Build.cs).
public static partial class PanelThemes
{
    public const string SystemId = "system", Dark = "dark", Light = "light", Demo = "cyberpunk-edgerunners", PixelBlocks = "pixel-blocks";
    public const string IslandHoliday = "island-holiday", IslandHoliday2 = "island-holiday-2";
    public const double TextContrast = 4.5, StrongTextContrast = 7, GraphicContrast = 3, IndicatorContrast = 2, ThumbContrast = 2;
    // The warning and critical colors, the theme's and those chosen for an application or the collapsed readings: they color a
    // quota's number and a reading's value as well as bars and rings, so they are held to the body-text minimum everywhere,
    // and a number and its bar keep one color.
    public const double LevelContrast = TextContrast;
    // The scroll bar's handle at rest is drawn at this opacity or, where that would leave it under ThumbContrast, at the least
    // opacity in steps of 0.05 that reaches it (PanelPalette.ThumbRestOpacity).
    public const double ThumbRest = .55;
    public static double ThumbRestOpacity(ThemeColor thumb, IReadOnlyList<ThemeColor> surfaces)
    {
        for (double opacity = ThumbRest; opacity < 1; opacity = Math.Round(opacity + .05, 2))
            if (thumb.WithAlpha((byte)Math.Round(thumb.A * opacity)).MinimumContrast(surfaces) >= ThumbContrast) return opacity;
        return 1;
    }

    public static readonly IReadOnlyList<PanelThemeToken> Tokens =
    [
        new("surface", "Panel surface", "Panel surface"),
        new("textPrimary", "Primary text", "Primary text", StrongTextContrast, PanelThemeBackdrop.SurfaceAndPopup),
        new("textSecondary", "Secondary text", "Secondary text", TextContrast, PanelThemeBackdrop.SurfaceAndPopup),
        // Tertiary text is small real text (the clock, "used", times, a notice's source), so it is held to the body-text minimum.
        new("textTertiary", "Tertiary text", "Tertiary text", TextContrast, PanelThemeBackdrop.SurfaceAndPopup),
        new("accent", "Accent (keyboard focus, chosen dates)", "Accent (keyboard focus, chosen dates)", GraphicContrast, PanelThemeBackdrop.SurfaceAndPopup),
        new("onAccent", "Text on the accent (chosen dates, a notice's main button)", "Text on the accent (chosen dates, a notice's main button)", TextContrast, PanelThemeBackdrop.Accent),
        new("danger", "Errors and paused sync", "Errors and paused sync", TextContrast, PanelThemeBackdrop.SurfaceAndPopup),
        new("liked", "Liked song", "Liked song", GraphicContrast, PanelThemeBackdrop.Surface),
        new("clock", "Default clock color", "Default clock color", TextContrast, PanelThemeBackdrop.Surface),
        new("equalizer", "Default equalizer color", "Default equalizer color", GraphicContrast, PanelThemeBackdrop.Surface),
        new("hairline", "Dividers", "Dividers"),
        new("track", "Progress track", "Progress track"),
        new("ringTrack", "Quota ring track", "Quota ring track"),
        new("fill", "Segment background", "Segment background"),
        new("fillSelected", "Selected segment", "Selected segment"),
        new("hover", "Hover", "Hover"),
        new("pressed", "Pressed", "Pressed"),
        new("outline", "Outlines", "Outlines"),
        new("popup", "Pop-up surface", "Pop-up surface"),
        new("popupBorder", "Pop-up border", "Pop-up border"),
        new("input", "Input field", "Input field"),
        new("rangeFill", "Date range fill", "Date range fill"),
        new("coverPlaceholder", "Missing cover", "Missing cover"),
        new("coverOutline", "Cover outline", "Cover outline"),
        new("scrollThumb", "Scroll bar", "Scroll bar", ThumbContrast, PanelThemeBackdrop.Surface),
        new("rim", "Rim", "Rim"),
        new("shadow", "Shadow or glow", "Shadow or glow"),
        new("indicator", "Indicator", "Indicator"),
        new("indicatorProgress", "Indicator progress", "Indicator progress", IndicatorContrast, PanelThemeBackdrop.Indicator),
        new("switchFill", "Page switch background", "Page switch background"),
        new("seekRail", "Seek bar track", "Seek bar track"),
        new("rowDivider", "Detail row dividers", "Detail row dividers"),
        new("rowHover", "Detail row hover", "Detail row hover"),
        new("menuDetail", "Song titles in menus", "Song titles in menus", GraphicContrast, PanelThemeBackdrop.SurfaceAndPopup),
        new("tooltip", "Tooltip surface", "Tooltip surface"),
        new("tooltipBorder", "Tooltip border", "Tooltip border"),
        new("bubble", "Seek time bubble", "Seek time bubble"),
        new("bubbleBorder", "Seek time bubble border", "Seek time bubble border"),
    ];
    // Applications keyed by the names the "apps" object uses. Sources entered by hand share "manual".
    public static readonly IReadOnlyList<string> AppKeys = ["claude", "codex", "antigravity", "cursor", "zcode", "dsh", "grokBot", "kimi", "opencode", "manual", "other", "warning", "critical"];
    public static string AppKey(ProviderId id) => id switch
    {
        ProviderId.Claude => "claude", ProviderId.Codex => "codex", ProviderId.Antigravity => "antigravity", ProviderId.Cursor => "cursor",
        ProviderId.ZCode => "zcode", ProviderId.Dsh => "dsh", ProviderId.GrokBot => "grokBot", ProviderId.Kimi => "kimi", ProviderId.OpenCode => "opencode",
        ProviderId.GlmChina or ProviderId.GlmGlobal or ProviderId.DeepSeek or ProviderId.KimiApi or ProviderId.OpenCodeGo or ProviderId.OpenRouter or ProviderId.NewApi or ProviderId.Sub2Api or ProviderId.Custom => "manual",
        _ => "other"
    };

    // Source-generated metadata, so the first theme read at startup does not build it by reflection.
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, RespectNullableAnnotations = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, TypeInfoResolver = PanelThemeJson.Default
    };
    public static PanelThemeFile Parse(string json)
    {
        var file = JsonSerializer.Deserialize<PanelThemeFile>(json, Json) ?? throw new JsonException("The theme file is empty.");
        // Properties are checked for null by the serializer; values inside the colors, apps and gradient collections are not.
        static bool HasNull(IEnumerable<string?>? values) => values?.Any(value => value is null) == true;
        // The night look is a layer of the same format and is checked the same way.
        static bool AnyNull(PanelThemeFile file) => HasNull(file.Colors?.Values) || HasNull(file.Apps?.Values) || HasNull(file.Swatches)
            || HasNull(file.Effects?.SurfaceGradient) || HasNull(file.Effects?.RimGradient)
            || HasNull(file.Clock?.Gradient) || HasNull(file.Clock?.Palette?.Values) || file.Clock?.Gradients?.Any(gradient => gradient is null || HasNull(gradient.Stops)) == true;
        if (AnyNull(file) || file.Night is { } night && AnyNull(night))
            throw new JsonException("A color cannot be null");
        return file;
    }

    // Built-in themes are read on demand: startup reads only the chosen theme and its base themes. The id of each is its resource
    // name, which a test keeps equal to the id inside the file.
    private const string ThemeResources = ".Themes.";
    // Dark and light first, then the ported editor themes, then BrimDeck's own extensions with the custom demo last.
    private static readonly string[] BuiltInOrder = [Dark, Light, "one-dark-pro", "github-dark", "github-light", "dracula", "night-owl", IslandHoliday, IslandHoliday2, PixelBlocks, Demo];
    private static (string Id, string Resource)[]? _builtInIndex;
    private static (string Id, string Resource)[] BuiltInIndex => _builtInIndex ??= [.. typeof(PanelThemes).Assembly.GetManifestResourceNames()
        .Where(name => name.Contains(ThemeResources, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
        .Select(name => (Id: NormalizeId(name[(name.IndexOf(ThemeResources, StringComparison.Ordinal) + ThemeResources.Length)..^".json".Length]), Resource: name))
        .OrderBy(entry => Array.IndexOf(BuiltInOrder, entry.Id) is var i and >= 0 ? i : int.MaxValue).ThenBy(entry => entry.Id, StringComparer.Ordinal)];
    private static readonly Dictionary<string, PanelThemeInfo> ParsedBuiltIns = new(StringComparer.Ordinal);
    public static bool IsBuiltIn(string id) => BuiltInIndex.Any(entry => entry.Id == id);
    public static PanelThemeInfo? BuiltIn(string id)
    {
        if (ParsedBuiltIns.TryGetValue(id, out var known)) return known;
        foreach (var (key, resource) in BuiltInIndex)
        {
            if (key != id) continue;
            // The demo theme sits with the extensions, so the custom section holds only the user's own themes.
            var kind = id is Dark or Light ? PanelThemeKind.Basic : PanelThemeKind.Extension;
            return ParsedBuiltIns[id] = new PanelThemeInfo(id, kind, Parse(Resource(resource)), null, null);
        }
        return null;
    }
    private static IReadOnlyList<PanelThemeInfo>? _builtIns;
    public static IReadOnlyList<PanelThemeInfo> BuiltIns => _builtIns ??= [.. BuiltInIndex.Select(entry => BuiltIn(entry.Id)!)];
    private static string Resource(string name)
    {
        using var stream = typeof(PanelThemes).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"[^a-z0-9._-]+")] private static partial Regex IdSeparators();
    public static string NormalizeId(string? id)
    {
        var text = IdSeparators().Replace((id ?? "").Trim().ToLowerInvariant(), "-").Trim('-', '.');
        return text.Length > 64 ? text[..64] : text;
    }

    // The theme an id stands for, among the built-in themes and the given user themes. "system" follows the Windows app theme.
    // An id that names nothing, such as a theme whose file was removed, falls back to the dark theme; the setting is kept.
    public static PanelThemeInfo Find(string id, bool systemDark, IReadOnlyList<PanelThemeInfo> user)
    {
        id = NormalizeId(id);
        if (id == SystemId) id = systemDark ? Dark : Light;
        return BuiltIn(id) ?? user.FirstOrDefault(theme => theme.Usable && theme.Id == id) ?? BuiltIn(Dark)!;
    }
}
