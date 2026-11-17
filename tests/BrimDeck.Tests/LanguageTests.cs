using System.Globalization;
using BrimDeck.Core;

internal static class LanguageTests
{
    public static void Run(string root, Action<string, bool> check)
    {
        string[] cultures = ["zh-CN", "zh-TW", "en-US", "en-GB", "ja-JP", "fr-FR", "ko-KR"];
        check("every display language chooses English",
            cultures.All(name => Loc.SystemLanguage(new CultureInfo(name)) == Loc.English));
        var unknown = new DeckSettings { Language = "fr-FR" }; unknown.Normalize();
        check("an unknown saved language reads as English", unknown.Language == Loc.English);

        var fresh = new SettingsStore(Path.Combine(root, "language-first"), "0.1.0");
        var first = fresh.Load();
        check("a first start uses English", fresh.IsFirstStart && first.Language == Loc.English);

        var upgrade = new SettingsStore(Path.Combine(root, "language-upgrade"), "0.2.0");
        Directory.CreateDirectory(upgrade.DirectoryPath);
        File.WriteAllText(upgrade.FilePath, """{"AppVersion":"0.1.0","Language":"zh-CN","Width":900}""");
        check("an upgrade normalizes language to English", upgrade.Load().Language == Loc.English && !upgrade.IsFirstStart);
        File.WriteAllText(upgrade.FilePath, """{"AppVersion":"0.1.0","Language":"en-US"}""");
        check("an upgrade keeps English when it was chosen", upgrade.Load().Language == Loc.English);
        File.WriteAllText(upgrade.FilePath, "{ not json");
        var broken = upgrade.Load();
        check("unreadable settings fall back to English and warn", upgrade.IsFirstStart && broken.Language == Loc.English && upgrade.LoadWarning is not null);
        check("load warning is in English", !upgrade.LoadWarning!.Any(c => c is >= '一' and <= '鿿'));
        check("Loc.Label translates known labels and leaves others as-is", Loc.Label("5 小时额度") == "5-hour limit" && Loc.Label("Monthly") == "Monthly");
    }
}
