using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    internal const int NimbusPage = 16;

    private static readonly (NimbusOutfit Outfit, string Label)[] OutfitOptions =
    [
        (NimbusOutfit.Auto, "Auto (Seasonal)"),
        (NimbusOutfit.None, "None"),
        (NimbusOutfit.SantaHat, "Santa Hat"),
        (NimbusOutfit.PartyHat, "Party Hat"),
        (NimbusOutfit.WitchHat, "Witch Hat"),
        (NimbusOutfit.Sunglasses, "Sunglasses"),
        (NimbusOutfit.RoundGlasses, "Round Glasses"),
        (NimbusOutfit.Bow, "Ribbon Bow"),
        (NimbusOutfit.Scarf, "Winter Scarf"),
        (NimbusOutfit.Pumpkin, "Pumpkin")
    ];

    private void NimbusSettings()
    {
        SectionTitle("Companion & Wardrobe");
        var outfitCombo = new ComboBox
        {
            Width = 170,
            Style = (Style)FindResource("SettingsCombo"),
            ItemsSource = OutfitOptions.Select(o => o.Label).ToList(),
            SelectedIndex = Math.Max(0, Array.FindIndex(OutfitOptions, o => o.Outfit == S.NimbusOutfit))
        };
        outfitCombo.SelectionChanged += (_, _) =>
        {
            if (outfitCombo.SelectedIndex >= 0 && outfitCombo.SelectedIndex < OutfitOptions.Length)
                Change(s => s.NimbusOutfit = OutfitOptions[outfitCombo.SelectedIndex].Outfit);
        };

        Group(
            Toggle("Enable Nimbus AI companion", S.NimbusEnabled,
                value => Change(s => s.NimbusEnabled = value)),
            Row("Outfit / Wardrobe", outfitCombo,
                help: () => "Auto (Seasonal) automatically switches outfits for Halloween, Christmas, New Year, and Easter."),
            Toggle("Show update hint on Nimbus", S.NimbusUpdateHintEnabled,
                value => Change(s => s.NimbusUpdateHintEnabled = value),
                help: "Shows a subtle update indicator on Nimbus while idle or sleeping when a new release is available (and morphs into a mailbox when ready)."),
            Toggle("Collapse idle notch to Nimbus only", S.NimbusCollapseToCharacter,
                value => Change(s => s.NimbusCollapseToCharacter = value),
                help: "Shrinks the collapsed island down to just Nimbus when not hovered. Turn off to always keep the full compact bar visible."),
            Toggle("Hide notch while sleeping", S.NimbusHideWhenIdle,
                value => Change(s => s.NimbusHideWhenIdle = value),
                help: "Fades out the notch while Nimbus is sleeping with no active tasks. Reappears on top-edge hover or when an agent wakes up."),
            Toggle("Do Not Disturb", S.NimbusDoNotDisturb,
                value => Change(s => s.NimbusDoNotDisturb = value),
                help: "Mutes Nimbus sound effects and suppresses non-urgent desktop speech bubbles.")
        );

        SectionTitle("Animations & Behavior");
        var sleepDelay = SliderRow(
            "Auto-sleep delay",
            S.NimbusAutoSleepMinutes, 1, 30, 1, "min",
            value => Change(s => s.NimbusAutoSleepMinutes = (int)value));

        static void SetRowEnabled(Grid row, bool enabled)
        {
            foreach (var control in row.Children.OfType<FrameworkElement>().Where(c => Grid.GetColumn(c) > 0))
                control.IsEnabled = enabled;
            foreach (var part in row.Children.OfType<FrameworkElement>().Where(c => c is not Slider))
                part.Opacity = enabled ? 1 : .4;
        }
        SetRowEnabled(sleepDelay, S.NimbusAutoSleepEnabled);

        Group(
            Toggle("Dance to music (112 BPM)", S.NimbusDanceEnabled,
                value => Change(s => s.NimbusDanceEnabled = value)),
            Toggle("Launch drop-in greeting", S.NimbusLaunchGreetingEnabled,
                value => Change(s => s.NimbusLaunchGreetingEnabled = value)),
            Toggle("Sound effects", S.NimbusSoundsEnabled,
                value => Change(s => s.NimbusSoundsEnabled = value)),
            Toggle("Auto-sleep on inactivity", S.NimbusAutoSleepEnabled,
                value => { Change(s => s.NimbusAutoSleepEnabled = value); SetRowEnabled(sleepDelay, value); }),
            sleepDelay
        );

        SectionTitle("Desktop Roaming & Multi-Agent");
        var returnBtn = ActionButton("Return Nimbus to Island", () =>
        {
            foreach (var deck in _app.Decks.ToList())
                deck.ReturnNimbusToIsland(clearSavedPosition: true);
        });

        Group(
            Toggle("Allow dragging Nimbus onto desktop", S.NimbusDesktopRoamingEnabled,
                value => Change(s => s.NimbusDesktopRoamingEnabled = value),
                help: "Drag Nimbus out of the notch or panel to place him anywhere on your desktop. Double-click to return, or he will auto-retract on urgent approvals/errors."),
            Row("Desktop position", returnBtn),
            Toggle("Mini clouds for parallel tasks", S.NimbusMiniBotsEnabled,
                value => Change(s => s.NimbusMiniBotsEnabled = value),
                help: "Displays mini Nimbus clouds in the expanded header when multiple agent sessions or subagents run concurrently.")
        );
    }
}
