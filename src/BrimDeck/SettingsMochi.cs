using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    internal const int MochiPage = 16;

    private static readonly (MochiOutfit Outfit, string Label)[] OutfitOptions =
    [
        (MochiOutfit.Auto, "Auto (Seasonal)"),
        (MochiOutfit.None, "None"),
        (MochiOutfit.SantaHat, "Santa Hat"),
        (MochiOutfit.PartyHat, "Party Hat"),
        (MochiOutfit.WitchHat, "Witch Hat"),
        (MochiOutfit.Sunglasses, "Sunglasses"),
        (MochiOutfit.RoundGlasses, "Round Glasses"),
        (MochiOutfit.Bow, "Ribbon Bow"),
        (MochiOutfit.Scarf, "Winter Scarf"),
        (MochiOutfit.Pumpkin, "Pumpkin")
    ];

    private void MochiSettings()
    {
        SectionTitle(Loc.T("伙伴与外观", "Companion & Wardrobe"));
        var outfitCombo = new ComboBox
        {
            Width = 170,
            Style = (Style)FindResource("SettingsCombo"),
            ItemsSource = OutfitOptions.Select(o => o.Label).ToList(),
            SelectedIndex = Math.Max(0, Array.FindIndex(OutfitOptions, o => o.Outfit == S.MochiOutfit))
        };
        outfitCombo.SelectionChanged += (_, _) =>
        {
            if (outfitCombo.SelectedIndex >= 0 && outfitCombo.SelectedIndex < OutfitOptions.Length)
                Change(s => s.MochiOutfit = OutfitOptions[outfitCombo.SelectedIndex].Outfit);
        };

        Group(
            Toggle(Loc.T("启用 Mochi AI 伙伴", "Enable Mochi AI companion"), S.MochiEnabled,
                value => Change(s => s.MochiEnabled = value)),
            Row(Loc.T("装扮衣橱", "Outfit / Wardrobe"), outfitCombo,
                help: () => Loc.T("“自动（季节限定）”会在万圣节、圣诞节、新年与复活节自动切换装扮。",
                    "Auto (Seasonal) automatically switches outfits for Halloween, Christmas, New Year, and Easter.")),
            Toggle(Loc.T("更新提示徽章", "Show update hint on Mochi"), S.MochiUpdateHintEnabled,
                value => Change(s => s.MochiUpdateHintEnabled = value),
                help: Loc.T("有新版本可用时，在 Mochi 空闲或睡眠时显示提示微光（就绪后切换为信箱形态）。",
                    "Shows a subtle update indicator on Mochi while idle or sleeping when a new release is available (and morphs into a mailbox when ready)."))
        );

        SectionTitle(Loc.T("动作与交互", "Animations & Behavior"));
        var sleepDelay = SliderRow(
            Loc.T("空闲入睡时间", "Auto-sleep delay"),
            S.MochiAutoSleepMinutes, 1, 30, 1, Loc.T("分钟", "min"),
            value => Change(s => s.MochiAutoSleepMinutes = (int)value));

        static void SetRowEnabled(Grid row, bool enabled)
        {
            foreach (var control in row.Children.OfType<FrameworkElement>().Where(c => Grid.GetColumn(c) > 0))
                control.IsEnabled = enabled;
            foreach (var part in row.Children.OfType<FrameworkElement>().Where(c => c is not Slider))
                part.Opacity = enabled ? 1 : .4;
        }
        SetRowEnabled(sleepDelay, S.MochiAutoSleepEnabled);

        Group(
            Toggle(Loc.T("音乐节拍跳舞 (112 BPM)", "Dance to music (112 BPM)"), S.MochiDanceEnabled,
                value => Change(s => s.MochiDanceEnabled = value)),
            Toggle(Loc.T("启动欢迎动画", "Launch drop-in greeting"), S.MochiLaunchGreetingEnabled,
                value => Change(s => s.MochiLaunchGreetingEnabled = value)),
            Toggle(Loc.T("交互音效", "Sound effects"), S.MochiSoundsEnabled,
                value => Change(s => s.MochiSoundsEnabled = value)),
            Toggle(Loc.T("空闲自动入睡", "Auto-sleep on inactivity"), S.MochiAutoSleepEnabled,
                value => { Change(s => s.MochiAutoSleepEnabled = value); SetRowEnabled(sleepDelay, value); }),
            sleepDelay
        );

        SectionTitle(Loc.T("桌面漫游与多任务", "Desktop Roaming & Multi-Agent"));
        var returnBtn = ActionButton(Loc.T("回到灵动岛", "Return Mochi to Island"), () =>
        {
            foreach (var deck in _app.Decks.ToList())
                deck.ReturnMochiToIsland(clearSavedPosition: true);
        });

        Group(
            Toggle(Loc.T("允许拖出到桌面", "Allow dragging Mochi onto desktop"), S.MochiDesktopRoamingEnabled,
                value => Change(s => s.MochiDesktopRoamingEnabled = value),
                help: Loc.T("将 Mochi 拖出刘海或面板即可自由放置在桌面上；双击回到灵动岛，有紧急审批/错误时也会自动归位。",
                    "Drag Mochi out of the notch or panel to place him anywhere on your desktop. Double-click to return, or he will auto-retract on urgent approvals/errors.")),
            Row(Loc.T("桌面位置", "Desktop position"), returnBtn),
            Toggle(Loc.T("并行任务迷你 Mochi", "Mini Mochis for parallel tasks"), S.MochiMiniBotsEnabled,
                value => Change(s => s.MochiMiniBotsEnabled = value),
                help: Loc.T("当多个 Agent 会话或子任务同时运行时，在展开面板顶部显示对应的迷你 Mochi。",
                    "Displays mini Mochi companions in the expanded header when multiple agent sessions or subagents run concurrently."))
        );
    }
}
