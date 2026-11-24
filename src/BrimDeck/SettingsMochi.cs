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
        SectionTitle("Companion & Wardrobe");
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
            Toggle("Enable Mochi AI companion", S.MochiEnabled,
                value => Change(s => s.MochiEnabled = value)),
            Row("Outfit / Wardrobe", outfitCombo,
                help: () => "Auto (Seasonal) automatically switches outfits for Halloween, Christmas, New Year, and Easter."),
            Toggle("Show update hint on Mochi", S.MochiUpdateHintEnabled,
                value => Change(s => s.MochiUpdateHintEnabled = value),
                help: "Shows a subtle update indicator on Mochi while idle or sleeping when a new release is available (and morphs into a mailbox when ready)."),
            Toggle("Collapse idle notch to Mochi only", S.MochiCollapseToCharacter,
                value => Change(s => s.MochiCollapseToCharacter = value),
                help: "Shrinks the collapsed island down to just Mochi when not hovered. Turn off to always keep the full compact bar visible."),
            Toggle("Hide notch while sleeping", S.MochiHideWhenIdle,
                value => Change(s => s.MochiHideWhenIdle = value),
                help: "Fades out the notch while Mochi is sleeping with no active tasks. Reappears on top-edge hover or when an agent wakes up."),
            Toggle("Do Not Disturb", S.MochiDoNotDisturb,
                value => Change(s => s.MochiDoNotDisturb = value),
                help: "Mutes Mochi sound effects and suppresses non-urgent desktop speech bubbles.")
        );

        SectionTitle("Animations & Behavior");
        var sleepDelay = SliderRow(
            "Auto-sleep delay",
            S.MochiAutoSleepMinutes, 1, 30, 1, "min",
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
            Toggle("Dance to music (112 BPM)", S.MochiDanceEnabled,
                value => Change(s => s.MochiDanceEnabled = value)),
            Toggle("Launch drop-in greeting", S.MochiLaunchGreetingEnabled,
                value => Change(s => s.MochiLaunchGreetingEnabled = value)),
            Toggle("Sound effects", S.MochiSoundsEnabled,
                value => Change(s => s.MochiSoundsEnabled = value)),
            Toggle("Auto-sleep on inactivity", S.MochiAutoSleepEnabled,
                value => { Change(s => s.MochiAutoSleepEnabled = value); SetRowEnabled(sleepDelay, value); }),
            sleepDelay
        );

        SectionTitle("Desktop Roaming & Multi-Agent");
        var returnBtn = ActionButton("Return Mochi to Island", () =>
        {
            foreach (var deck in _app.Decks.ToList())
                deck.ReturnMochiToIsland(clearSavedPosition: true);
        });

        Group(
            Toggle("Allow dragging Mochi onto desktop", S.MochiDesktopRoamingEnabled,
                value => Change(s => s.MochiDesktopRoamingEnabled = value),
                help: "Drag Mochi out of the notch or panel to place him anywhere on your desktop. Double-click to return, or he will auto-retract on urgent approvals/errors."),
            Row("Desktop position", returnBtn),
            Toggle("Mini Mochis for parallel tasks", S.MochiMiniBotsEnabled,
                value => Change(s => s.MochiMiniBotsEnabled = value),
                help: "Displays mini Mochi companions in the expanded header when multiple agent sessions or subagents run concurrently.")
        );
    }
}
