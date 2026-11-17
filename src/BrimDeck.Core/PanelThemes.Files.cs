using System.Text.Json;

namespace BrimDeck.Core;

// Theme files in the user's folder, and the checks that decide whether a file can be used.
public static partial class PanelThemes
{
    private const int MaximumFileBytes = 256 * 1024;

    // A theme's name on disk: the file name without ".json", or the folder's name for a theme.json in a subfolder.
    public static string FileName(string path) => Path.GetFileName(path) == "theme.json" ? Path.GetFileName(Path.GetDirectoryName(path)) ?? "" : Path.GetFileNameWithoutExtension(path);
    // The same as it is shown in a list: "folder/theme.json" or "name.json".
    public static string DisplayFileName(string path) => Path.GetFileName(path) == "theme.json" ? FileName(path) + "/theme.json" : Path.GetFileName(path);

    // Themes in the user's folder: "*.json" in the folder itself and "theme.json" in each subfolder, like an editor's
    // theme extensions. A file that cannot be used is still listed, with the reason, so the user can see why.
    public static IReadOnlyList<PanelThemeInfo> LoadFolder(string folder)
    {
        List<string> files;
        try
        {
            if (!Directory.Exists(folder)) return [];
            files = Directory.EnumerateFiles(folder, "*.json").Concat(Directory.EnumerateDirectories(folder).Select(dir => Path.Combine(dir, "theme.json")).Where(File.Exists))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        // Every file is read and checked before any id is handed out.
        var read = new List<(string Path, string Name, PanelThemeFile File, string Id, string? Error)>();
        foreach (var path in files)
        {
            string name = NormalizeId(FileName(path));
            try
            {
                if (new FileInfo(path).Length > MaximumFileBytes)
                    throw new InvalidDataException($"The file is larger than {MaximumFileBytes / 1024} KB");
                var file = Parse(File.ReadAllText(path));
                var id = NormalizeId(file.Id.Length > 0 ? file.Id : name);
                read.Add((path, name, file, id, FileProblem(file, id)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException or ArgumentException)
            {
                read.Add((path, name, new PanelThemeFile { Name = Path.GetFileName(path) }, name, ex.Message));
            }
        }
        // A usable file takes its id; a file named after its id claims it before any other, so a copy made in Explorer
        // ("my-theme - Copy.json") never takes the id from the original. A later file with the same id is refused.
        var taken = BuiltInIndex.Select(entry => entry.Id).Append(SystemId).ToHashSet(StringComparer.Ordinal);
        // A file named after its id keeps that id while it cannot be used (being saved, say), so a copy does not take its place.
        foreach (var entry in read) if (entry.Error is not null && entry.Name == entry.Id && entry.Id.Length > 0) taken.Add(entry.Id);
        var owner = new bool[read.Count];
        foreach (int i in Enumerable.Range(0, read.Count).OrderBy(i => read[i].Name == read[i].Id ? 0 : 1).ThenBy(i => i))
            owner[i] = read[i].Error is null && taken.Add(read[i].Id);
        var found = new List<PanelThemeInfo>();
        for (int i = 0; i < read.Count; i++)
        {
            var (path, name, file, id, error) = read[i];
            if (error is null && !owner[i]) error = $"The theme id \"{id}\" is already used by another theme";
            // An unusable entry is listed under an id no theme can have, unique to its file.
            found.Add(new PanelThemeInfo(error is null ? id : "file:" + (id.Length > 0 ? id : name) + ":" + i, PanelThemeKind.Custom, file, path, error));
        }
        return found;
    }

    // Themes of the user's in the order they were added: by the time their files were created, then by name. A file that cannot
    // be read for its time goes last.
    public static IEnumerable<PanelThemeInfo> InAddedOrder(IEnumerable<PanelThemeInfo> themes)
    {
        static DateTime Created(string? path)
        {
            try { return path is not null && File.Exists(path) ? File.GetCreationTimeUtc(path) : DateTime.MaxValue; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return DateTime.MaxValue; }
        }
        return themes.Select(theme => (Theme: theme, Created: Created(theme.Path))).OrderBy(entry => entry.Created)
            .ThenBy(entry => entry.Theme.Path, StringComparer.OrdinalIgnoreCase).Select(entry => entry.Theme);
    }

    // Why a theme file cannot be used, or null. Colors that only need adjusting are not problems (see Build).
    private static string? FileProblem(PanelThemeFile file, string id)
    {
        // Letters outside a-z (Chinese, say) would vanish from the id, or two ids would become one; the file says so instead.
        if (file.Id.Any(ch => ch > 127))
            return $"The theme id \"{file.Id}\" may only use the letters a–z, digits, - _ and . (a Chinese name goes in \"name\")";
        if (id.Length == 0) return "The theme has no id";
        if (file.FormatVersion > 1) return "This theme needs a newer BrimDeck";
        var baseId = NormalizeId(file.Base);
        if (baseId.Length > 0 && !IsBuiltIn(baseId)) return $"The base theme \"{file.Base}\" does not exist";
        if (LayerProblem(file) is { } problem) return problem;
        if (file.Night is { } night && LayerProblem(night) is { } nightProblem) return "Night look: " + nightProblem;
        foreach (var time in new[] { file.Daylight?.Day, file.Daylight?.Night })
            if (time is { Length: > 0 } && ParseTimeOfDay(time) is null) return $"The time \"{time}\" is not HH:mm";
        return null;
    }

    // What makes the colors, effects or clock of a file, or of its night look, unusable.
    private static string? LayerProblem(PanelThemeFile file)
    {
        foreach (var (key, value) in file.Colors ?? [])
        {
            if (Tokens.All(token => !string.Equals(token.Key, key, StringComparison.OrdinalIgnoreCase))) return $"Unknown color \"{key}\"";
            if (!ThemeColor.TryParse(value, out _)) return $"The color \"{key}\" has the value \"{value}\", which is not #RRGGBB or #AARRGGBB";
        }
        foreach (var (key, value) in file.Apps ?? [])
        {
            if (AppKeys.All(app => !string.Equals(app, key, StringComparison.OrdinalIgnoreCase))) return $"Unknown app color \"{key}\"";
            if (!ThemeColor.TryParse(value, out _)) return $"The app color \"{key}\" has the invalid value \"{value}\"";
        }
        if (file.Swatches is { } swatches && (swatches.Count > 12 || swatches.Any(value => !ThemeColor.TryParse(value, out _))))
            return "The picker swatches must be at most twelve valid colors";
        var decor = file.Effects?.Decor;
        var decorColors = new[] { decor?.Horizon?.Sea, decor?.Horizon?.Line, decor?.Caustics?.Color, decor?.Palm?.Color }.OfType<string>();
        foreach (var value in (file.Effects?.SurfaceGradient ?? []).Concat(file.Effects?.RimGradient ?? []).Concat(decorColors))
            if (!ThemeColor.TryParse(value, out _)) return $"The effect color \"{value}\" is invalid";
        return file.Clock is { } clock ? ClockProblem(clock) : null;
    }

    private static string? ClockProblem(PanelThemeClock clock)
    {
        if (clock.Style.Length > 0 && !Enum.TryParse<ClockStyle>(clock.Style, true, out _)) return $"Unknown clock style \"{clock.Style}\"";
        if (clock.Color.Length > 0 && ParseClockColor(clock.Color) is null) return $"Unknown clock color \"{clock.Color}\"";
        if (clock.Custom.Length > 0 && !ThemeColor.TryParse(clock.Custom, out _)) return $"The custom clock color \"{clock.Custom}\" is invalid";
        if (clock.Gradient is { } stops && (stops.Count != 3 || stops.Any(stop => !ThemeColor.TryParse(stop, out _))))
            return "The clock gradient must be exactly three valid colors";
        foreach (var (key, value) in clock.Palette ?? [])
        {
            if (ClockColors.Swatches.All(color => ClockColors.Key(color) != key)) return $"Unknown clock swatch \"{key}\"";
            if (!ThemeColor.TryParse(value, out _)) return $"The clock swatch \"{key}\" has the invalid value \"{value}\"";
        }
        if (clock.Gradients is { } gradients && (gradients.Count is 0 or > 8 || gradients.Any(g => g.Stops is not { Count: 3 } || g.Stops.Any(stop => !ThemeColor.TryParse(stop, out _)))))
            return "The preset gradients must be one to eight, each exactly three valid colors";
        return null;
    }
}
