using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    // keepPreview marks the segments alone as preview controls, so a press on the rest of the row still ends the preview.
    private FrameworkElement MusicChoice<T>(string title, T selected, (T Value, string Label)[] options, Action<T> change, bool enabled = true, bool keepPreview = false, string? help = null, double width = 292) where T : notnull
    {
        var strip = new UniformGrid { Columns = options.Length, Width = width, IsEnabled = enabled };
        if (keepPreview) KeepPreview(strip);
        foreach (var option in options)
        {
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = option.Label, GroupName = title, IsChecked = EqualityComparer<T>.Default.Equals(option.Value, selected) };
            AutomationProperties.SetName(choice, title + " " + option.Label); choice.Checked += (_, _) => change(option.Value); strip.Children.Add(choice);
        }
        var row = Row(title, SegmentBorder(strip), help: help is null ? null : () => help); row.IsEnabled = enabled; return row;
    }
    // The options under the style cards belong to the chosen style. Choosing another card slides the current options
    // out and the chosen style's options in, moving the way the chosen card lies from the previous one.
    private Grid? _styleOptions;
    private void ShowStyleOptions(CompactStyle style, CompactStyle? previous)
    {
        if (_styleOptions is not { } host) return;
        var incoming = Collect(() => { StyleOptions(style); CollapsedSizeOptions(style); });
        var outgoing = host.Children.OfType<FrameworkElement>().LastOrDefault();
        double width = host.ActualWidth;
        if (outgoing is null || previous is not { } from || from == style || width <= 0 || !SystemParameters.ClientAreaAnimation)
        { host.Children.Clear(); host.Children.Add(incoming); return; }
        // A panel still leaving from an earlier choice is dropped; the one arriving becomes the one leaving.
        foreach (var leaving in host.Children.OfType<FrameworkElement>().Where(child => child != outgoing).ToList()) host.Children.Remove(leaving);
        double direction = style > from ? 1 : -1;
        host.ClipToBounds = true; outgoing.IsHitTestVisible = false; host.Children.Add(incoming);
        var leave = outgoing.RenderTransform as TranslateTransform ?? new TranslateTransform(); outgoing.RenderTransform = leave;
        var enter = new TranslateTransform(direction * width, 0); incoming.RenderTransform = enter;
        DoubleAnimation Slide(double to) => new(to, TimeSpan.FromMilliseconds(300)) { EasingFunction = Soft.Out };
        var leaveMotion = Slide(-direction * width); leaveMotion.Completed += (_, _) => host.Children.Remove(outgoing);
        var enterMotion = Slide(0); enterMotion.Completed += (_, _) => { if (host.Children.Count == 1) host.ClipToBounds = false; };
        leave.BeginAnimation(TranslateTransform.XProperty, leaveMotion); enter.BeginAnimation(TranslateTransform.XProperty, enterMotion);
    }
    private void StyleOptions(CompactStyle style)
    {
        SectionTitle("Content · " + StyleName(style));
        if (style == CompactStyle.Line)
        {
            Group(Toggle("Show playback progress", S.MusicIndicatorProgress, v => Change(s => s.MusicIndicatorProgress = v)));
            return;
        }
        // The rows are in the order the collapsed row shows the parts, left to right; only the cover keeps its place at the left.
        var rows = new StackPanel();
        _partOptions.Clear();
        foreach (var part in S.CompactOrder(style))
            rows.Children.Add(part switch { CompactPart.Clock => ClockRow(style, rows), _ => PartRow(style, part, rows) });
        UpdateDividers(rows);
        Group(rows);
    }

    private const double PartRowHeight = 44, PartHandleWidth = 40;
    // The parts whose options are open (the clock, the quota rings), kept while the page is rebuilt or the collapsed style changes.
    private readonly HashSet<CompactPart> _openParts = [];
    private readonly List<(Border Row, Border Options, Button Arrow)> _partOptions = [];
    // One draggable row of the collapsed content: the handle, the name and the switch.
    private Border PartRow(CompactStyle style, CompactPart part, StackPanel rows)
    {
        bool notch = style == CompactStyle.Notch;
        var (title, shown, help) = part switch
        {
            CompactPart.Rings => ("Quota rings", S.ShowsSummary(style), null),
            CompactPart.Equalizer => ("Equalizer", S.ShowsEqualizer(style), null),
            CompactPart.Music => ("Music now playing", S.ShowsMusic(style),
                "Dragging moves only the title or lyric. The cover stays at the left."),
            CompactPart.Transfer => ("File transfer", style == CompactStyle.Notch ? S.NotchTransfer : S.CapsuleTransfer,
                "Shows how many files the file transfer holds, and nothing while it is empty. Clicking it opens the clipboard and file transfer page."),
            _ => ("System monitor", S.ShowsMonitor(style), null)
        };
        void Show(bool on) => Change(s =>
        {
            switch (part)
            {
                case CompactPart.Rings: if (notch) s.NotchSummary = on; else s.CapsuleSummary = on; break;
                case CompactPart.Music: if (notch) s.NotchMusic = on; else s.CapsuleMusic = on; break;
                case CompactPart.Equalizer: if (notch) s.NotchEqualizer = on; else s.CapsuleEqualizer = on; break;
                case CompactPart.Transfer: if (notch) s.NotchTransfer = on; else s.CapsuleTransfer = on; break;
                default: if (notch) s.NotchMonitor = on; else s.CapsuleMonitor = on; break;
            }
        });
        var row = new Grid { Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PartHandleWidth) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var handle = new GripHandle { Width = 22, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, HorizontalAlignment = HorizontalAlignment.Left, Child = Grip(TextTertiary) };
        row.Children.Add(handle);
        var label = SettingLabel(title, help is null ? null : () => help); Grid.SetColumn(label, 1); row.Children.Add(label);
        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = shown, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, title); toggle.Checked += (_, _) => Show(true); toggle.Unchecked += (_, _) => Show(false);
        Grid.SetColumn(toggle, 2); row.Children.Add(toggle);
        AttachPartDrag(handle, title, style, rows);
        return new Border { Child = row, Height = PartRowHeight, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0), Tag = part, RenderTransform = new TranslateTransform() };
    }
    // Dropped in a new place, the collapsed row takes the new order at once. The open options close when a drag begins, so every
    // row is one line high while they move.
    private void AttachPartDrag(Border handle, string name, CompactStyle style, StackPanel rows) => AttachDrag(handle, name, () => rows, PartRowHeight, list =>
    {
        var order = list.Children.OfType<Border>().Where(b => b.Tag is CompactPart).Select(b => (CompactPart)b.Tag).ToArray();
        Change(s => s.SetCompactOrder(style, order));
    }, ClosePartOptions);
    private void ClosePartOptions()
    {
        _openParts.Clear();
        foreach (var (row, options, arrow) in _partOptions) { options.Visibility = Visibility.Collapsed; PointArrow(arrow, false); row.Height = PartRowHeight; }
    }
    // A part with options under it: the handle, the title with its arrow, and the switch, which only turns the part on or off.
    // The title and its arrow open and close the options; the options are dimmed while the part is off. Without padding they lie
    // under the title, a little indented, as the page rows' sizes do; with it, on a card across the row.
    private Border OptionsPartRow(CompactStyle style, CompactPart part, StackPanel rows, string title, string arrowName, bool shown, Action<bool> turn, FrameworkElement content, Thickness? padding = null)
    {
        var options = padding is { } card ? new Border { Background = UI.Brush(Inset), CornerRadius = new CornerRadius(10), Padding = card, Margin = new Thickness(0, 0, 0, 12), Child = content }
            : new Border { Margin = new Thickness(20, 0, 0, 8), Child = content };
        void Enable(bool on) { options.IsEnabled = on; options.Opacity = on ? 1 : .45; }
        Enable(shown);
        Button arrow = null!;
        Border item = null!;
        void Show(bool open)
        {
            if (open) _openParts.Add(part); else _openParts.Remove(part);
            options.Visibility = open ? Visibility.Visible : Visibility.Collapsed; PointArrow(arrow, open);
            item.Height = open ? double.NaN : PartRowHeight;
        }
        arrow = ChevronButton(arrowName, () => Show(!_openParts.Contains(part)));
        arrow.Margin = new Thickness(2, 0, 0, 0);
        var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(SettingLabel(title)); heading.Children.Add(arrow);
        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = shown, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, title);
        toggle.Checked += (_, _) => { turn(true); Enable(true); }; toggle.Unchecked += (_, _) => { turn(false); Enable(false); };
        var row = new Grid { Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PartHandleWidth) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PartRowHeight) }); row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var handle = new GripHandle { Width = 22, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, HorizontalAlignment = HorizontalAlignment.Left, Child = Grip(TextTertiary) };
        row.Children.Add(handle);
        Grid.SetColumn(heading, 1); row.Children.Add(heading);
        Grid.SetColumn(toggle, 2); row.Children.Add(toggle);
        Grid.SetRow(options, 1); if (padding is null) Grid.SetColumn(options, 1); Grid.SetColumnSpan(options, padding is null ? 2 : 3); row.Children.Add(options);
        // The handle, the switch and the arrow take their own clicks; a click anywhere else on the title line opens or closes the
        // options, as the size rows do.
        row.MouseLeftButtonDown += (_, e) => { if (e.GetPosition(row).Y >= PartRowHeight) return; Show(!_openParts.Contains(part)); e.Handled = true; };
        AttachPartDrag(handle, title, style, rows);
        item = new Border { Child = row, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0), Tag = part, RenderTransform = new TranslateTransform() };
        _partOptions.Add((item, options, arrow));
        Show(_openParts.Contains(part));
        return item;
    }
    // The quota rings' look, on the quota page beside the applications: one ring or nested rings, whether the arcs show what is used
    // or what is left, and whether each application's letter (its display name's) is written inside its ring. They are shared by the
    // notch, the capsule and the side column; each application's quotas are set with the application (RingDetails), and the rings are
    // switched on or off with the collapsed content (PartRow). A new style builds the application rows again, so their ring lines follow it.
    private void QuotaRingSection()
    {
        SectionTitle("Quota display");
        Group(MusicChoice("Ring style", S.QuotaRingStyle, [(QuotaRingStyle.Single, "Single"), (QuotaRingStyle.Nested, "Nested")],
                v => { Change(s => s.QuotaRingStyle = v); foreach (var entry in S.Apps) RefreshAppRow(entry.InstanceId); }, help: "Single: one ring per app, showing its most used quota by default.\nNested: up to three rings per app, each showing a different quota.\nWhich quota each ring shows is set by expanding the app below.", width: 200),
            MusicChoice("Used or left", S.QuotaRingRemaining, [(false, "Used"), (true, "Left")],
                v => Change(s => s.QuotaRingRemaining = v), help: "Used: shows the share used; the more is used, the longer the rings and bars.\nLeft: shows the share left; the more is used, the shorter the rings and bars.\nThe quota rings, the panel's bars and percentages, and the quota notices all follow it; amounts such as balances and spending stay as they are.", width: 200),
            Toggle("Letter in the rings", S.QuotaRingLetters, v => Change(s => s.QuotaRingLetters = v),
                help: "Shows the first letter of the app's name inside its quota ring, taken from the Display name column below."));
    }
    // The clock's swatches but the gradient: the theme's color (theme), the presets, a custom color and the cover's. Used for the
    // equalizer and for the music page's current lyric; save gets the chosen color and the custom one.
    private StackPanel ColorSwatches(string group, string label, ClockColor selected, string custom, Brush theme, Action<ClockColor, string> save)
    {
        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        ClockColor[] order = [ClockColor.White, ClockColor.Amber, ClockColor.Cyan, ClockColor.Pink, ClockColor.Lime, ClockColor.Custom, ClockColor.Cover];
        foreach (var color in order)
        {
            string name = ColorName(color);
            var swatch = new RadioButton { Style = (Style)FindResource("SettingsClockSwatch"), GroupName = group, IsChecked = selected == color,
                Background = SwatchFill(color), ToolTip = name, Margin = new Thickness(swatches.Children.Count == 0 ? 0 : 6, 0, 0, 0) };
            AutomationProperties.SetName(swatch, label + name);
            swatch.Checked += (_, _) => save(color, custom);
            if (color == ClockColor.Cover) swatch.Content = MusicVisuals.Icon("music", 13, UI.Brush(SwatchInk(SampleCover)));
            if (color == ClockColor.Custom)
            {
                // Choosing this swatch also opens the picker; each later click opens it again.
                swatch.Content = Pencil(ClockFace.Parse(custom));
                AttachClockPicker(swatch, () => custom, ClockColors.DefaultCustom, value =>
                {
                    custom = value; save(ClockColor.Custom, value);
                    swatch.Background = SwatchFill(ClockColor.Custom); swatch.Content = Pencil(ClockFace.Parse(value));
                });
            }
            swatches.Children.Add(swatch);
        }
        return swatches;
        Brush SwatchFill(ClockColor color) => color switch
        {
            ClockColor.Custom => ClockFace.Fill([ClockFace.Parse(custom)]),
            ClockColor.Cover => ClockFace.Fill([SampleCover]),
            ClockColor.White => theme,
            _ => ClockFace.Fill([PanelTheme.Readable(ClockFace.Preset(color))])
        };
    }
    // Settings every player shares: the equalizer, colors, cover and text.
    private void MusicStyle()
    {
        // The equalizer's way and color serve the collapsed and the expanded equalizer alike; each keeps its own length, and the
        // collapsed one's switch stays with the collapsed content (PartRow).
        SectionTitle("General");
        var pageBars = SliderRow("Expanded equalizer length", S.MusicEqualizerBars, CompactMusicLayout.MinimumBars, CompactMusicLayout.MaximumBars, 1, "bars",
            v => Change(s => s.MusicEqualizerBars = (int)v));
        pageBars.IsEnabled = S.MusicPage;
        Group(MusicChoice("Equalizer mode", S.EqualizerMode, [(EqualizerMode.Flow, "Flow"), (EqualizerMode.Spectrum, "Spectrum")],
                v => Change(s => s.EqualizerMode = v), help: "Flow: the rise and fall of the sound runs from left to right. Spectrum: each bar shows a range of pitch, low on the left."),
            Row("Equalizer color", ColorSwatches("EqualizerColor", "Equalizer color ", S.EqualizerColor, S.EqualizerCustomColor,
                UI.Brush(PanelTheme.Hex("equalizer")), (chosen, custom) => Change(s => { s.EqualizerColor = chosen; s.EqualizerCustomColor = custom; }))),
            SliderRow("Collapsed equalizer length", S.EqualizerBars, CompactMusicLayout.MinimumBars, CompactMusicLayout.MaximumBars, 1, "bars",
                v => Change(s => s.EqualizerBars = (int)v)),
            pageBars,
            MusicChoice("Collapsed equalizer shown", S.EqualizerAlways, [(false, "While music plays"), (true, "Always")],
                v => Change(s => s.EqualizerAlways = v), help: "Applies once the equalizer is switched on in the collapsed content. When always shown, it also shows while no music plays, reading all system sound."));
        SectionTitle("When collapsed");
        // The instrumental choice belongs to the lyric, so it shows only while the middle text is the lyric.
        var text = new List<FrameworkElement> { MusicChoice("Middle text", S.MusicText, new[] { (CompactMusicText.None, "None"), (CompactMusicText.Title, "Title"), (CompactMusicText.Lyrics, "Lyrics") },
            v => { Change(s => s.MusicText = v); ShowPage(_page); }) };
        if (S.MusicText == CompactMusicText.Lyrics)
            text.Add(Toggle("Show the title for instrumentals", S.MusicInstrumentalTitle, v => Change(s => s.MusicInstrumentalTitle = v),
                help: "Songs carry no reliable instrumental mark, so this is inferred from the lyrics and can be wrong."));
        Group([.. text,
            MusicChoice("Hide while paused", S.MusicPausedHide, new[] { (PausedMusicHide.None, "Nothing"), (PausedMusicHide.Text, "Text"),
                (PausedMusicHide.Cover, "Cover"), (PausedMusicHide.Both, "Both") }, v => Change(s => s.MusicPausedHide = v),
                help: "While music is paused, the chosen part is hidden when collapsed and comes back when playback resumes. \"Text\" is the title or lyric shown by \"Middle text\"; \"Both\" is the text and the cover, not the equalizer."),
            Toggle("Briefly show the title on a track change", S.MusicTrackNotice, v => Change(s => s.MusicTrackNotice = v)),
            Toggle("Show cover", S.MusicCompactCover, v => Change(s => s.MusicCompactCover = v))]);
        SectionTitle("When expanded");
        var lyricColor = Row("Current lyric color", ColorSwatches("MusicLyricColor", "Current lyric color ",
            S.MusicLyricColor, S.MusicLyricCustomColor, UI.Brush(PanelTheme.Hex("textPrimary")),
            (chosen, custom) => Change(s => { s.MusicLyricColor = chosen; s.MusicLyricCustomColor = custom; })));
        lyricColor.IsEnabled = S.MusicPage;
        Group(Toggle("Show cover", S.MusicPageCover, v => Change(s => s.MusicPageCover = v), S.MusicPage),
            Toggle("Show lyrics", S.LyricsEnabled, v => Change(s => s.LyricsEnabled = v), S.MusicPage), lyricColor);
    }
    // Operations only one player supports, grouped under that player's name.
    private void Players()
    {
        SectionTitle("Playback sources");
        Group(IgnoredAppsRow());
    }
    // Whether the ignored apps are shown under their row, kept while the row is rebuilt after an app is added or removed.
    private bool _ignoredOpen;
    // Holds the row, so that adding or removing an app rebuilds the row alone, as the app rules row does.
    private Border? _ignoredHost;

    // The applications whose playback the panel leaves out. The menu offers the apps that report playback now, and any
    // executable. An executable is added under its file name, the id most players report; a player that reports an id of
    // its own choosing is matched only while it plays and can be added from the menu then.
    private FrameworkElement IgnoredAppsRow() => _ignoredHost = new Border { Child = IgnoredAppsContent() };
    private void RefreshIgnoredApps() { if (_ignoredHost is not null) _ignoredHost.Child = IgnoredAppsContent(); }

    private FrameworkElement IgnoredAppsContent() => AppListRow("Ignored apps",
        () => "While an ignored app plays, the panel does not show its songs and the source menu leaves it out. \"Add app\" lists the apps that are playing or paused, or browse for an executable. Ignoring a browser ignores playback in all its pages.",
        "Show ignored apps", "Add an app to ignore",
        [.. S.IgnoredMediaApps.Select(IgnoredAppLine)], IgnoredAppMenu, _ignoredOpen, open => _ignoredOpen = open);

    private FrameworkElement IgnoredAppLine(IgnoredMediaApp app)
    {
        string name = app.Name.Length > 0 ? app.Name : app.AppId;
        return AppLine(name, IgnoredAppDetail(app), app.Path, app.Path.Length > 0 ? app.Path : app.AppId, () =>
        {
            Change(s => s.IgnoredMediaApps.RemoveAll(entry => string.Equals(entry.AppId, app.AppId, StringComparison.OrdinalIgnoreCase)));
            RefreshIgnoredApps();
        });
    }

    // The executable's file name when it is known, otherwise the id the app's media session reports.
    private static string IgnoredAppDetail(IgnoredMediaApp app) => app.Path.Length > 0 ? System.IO.Path.GetFileName(app.Path) : app.AppId;

    private void IgnoredAppMenu(Button anchor)
    {
        var menu = AppMenu(anchor);
        var apps = (((App)Application.Current).Media?.DetectedApps() ?? [])
            .Where(app => !S.IgnoredMediaApps.Any(entry => string.Equals(entry.AppId, app.AppId, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var app in apps)
            menu.Items.Add(AppMenuItem(app.Name, IgnoredAppDetail(app), app.Path, () =>
            {
                Change(s => s.IgnoredMediaApps.Add(app));
                RefreshIgnoredApps();
            }));
        if (apps.Count == 0) menu.Items.Add(new MenuItem { Header = "No app is playing", IsEnabled = false });
        AddBrowseItem(menu, "Choose an app to ignore", path =>
        {
            var app = new IgnoredMediaApp(System.IO.Path.GetFileName(path), AppName(System.IO.Path.GetFileNameWithoutExtension(path), path), path);
            if (S.IgnoredMediaApps.Any(entry => string.Equals(entry.AppId, app.AppId, StringComparison.OrdinalIgnoreCase))) return;
            Change(s => s.IgnoredMediaApps.Add(app));
            RefreshIgnoredApps();
        });
        menu.IsOpen = true;
    }

    // A line of small text between a player's title and its settings; last: the player has no settings, so the line ends the
    // section with a group's spacing.
    private void MusicSizeControls()
    {
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width = Dimension("Width", S.MusicWidth, 440, 1200, value => Resize(s => s.MusicWidth = value, DeckPage.Music)); width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension("Height", S.MusicHeight, 140, 400, value => Resize(s => s.MusicHeight = value, DeckPage.Music)); height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        Target.Children.Add(dimensions);
        var sizes = KeepPreview(new UniformGrid { Columns = 3, Width = 292 });
        foreach (var size in Enum.GetValues<PanelSize>())
        {
            var (w, h) = MusicSizes.For(size);
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = PanelSizes.Name(size), GroupName = "MusicSize", IsChecked = (S.MusicWidth, S.MusicHeight) == (w, h) };
            AutomationProperties.SetName(choice, "Music quick size " + PanelSizes.Name(size)); choice.Checked += (_, _) => { Resize(s => { s.MusicWidth = w; s.MusicHeight = h; }, DeckPage.Music); ShowPage(2); }; sizes.Children.Add(choice);
        }
        QuickSizeRow(sizes);
    }
}
