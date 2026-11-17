using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private Border? _updateBadge;
    private UpdateView? _aboutUpdateView;
    private Window? _updateDialog;
    private int _updateOpenGeneration;

    internal async Task CheckUpdatesOnOpenAsync()
    {
        int generation = ++_updateOpenGeneration;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (!IsLoaded || !IsVisible) return;
        await _app.Updates.CheckAsync(false, _app.Lifetime.Token);
        if (generation != _updateOpenGeneration || _app.Exiting || _updateDialog is not null) return;
        if (_app.Updates.ShouldPrompt(IsLoaded && IsVisible && WindowState != WindowState.Minimized, IsActive)) ShowUpdateDialog();
    }

    // The panel's update dot opens the About page and shows the update dialog, fetching the release details first if needed.
    internal async Task ShowUpdateNoticeAsync()
    {
        const int aboutPage = 7;
        int generation = ++_updateOpenGeneration;
        ShowPage(aboutPage);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (!IsLoaded || !IsVisible) return;
        if (!_app.Updates.HasUpdate) await _app.Updates.CheckAsync(true, _app.Lifetime.Token);
        if (generation != _updateOpenGeneration || _app.Exiting || _updateDialog is not null) return;
        ShowUpdateDialog();
    }

    private void UpdatesChanged()
    {
        if (Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(UpdatesChanged); return; }
        if (_updateBadge is not null) _updateBadge.Visibility = _app.Updates.HasUpdate ? Visibility.Visible : Visibility.Collapsed;
        _aboutUpdateView?.Refresh();
    }

    private Border UpdateBadge()
    {
        var badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(9, 0, 0, 0),
            Background = UI.Brush(Inset), VerticalAlignment = VerticalAlignment.Center, Visibility = _app.Updates.HasUpdate ? Visibility.Visible : Visibility.Collapsed,
            Child = TextLine("New", 9, _palette.Accent, FontWeights.Medium) };
        AutomationProperties.SetName(badge, "A new version is available"); return badge;
    }

    internal void ShowUpdateDialog()
    {
        if (_updateDialog is not null || !_app.Updates.HasUpdate || !IsVisible) return;
        var dialog = new Window { Title = "BrimDeck Update", Owner = this, Width = 510, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = Fonts.PlainOf(UiFontRole.Panel),
            Background = UI.Brush(Surface), Foreground = UI.Brush(TextPrimary) };
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BrimDeck;component/SettingsResources.xaml", UriKind.Relative) });
        _palette.Apply(dialog.Resources); Native.SettingsChrome.Apply(dialog); dialog.ResizeMode = ResizeMode.NoResize;
        dialog.SourceInitialized += (_, _) => Native.SettingsChrome.SetTheme(dialog, _palette.Dark);
        var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 24) };
        var title = TextLine("Software update", 21, TextPrimary, FontWeights.SemiBold); title.Margin = new Thickness(0, 0, 0, 22); panel.Children.Add(title);
        var view = CreateUpdateView(false); panel.Children.Add(view.Root);
        var close = ActionButton("Later", dialog.Close); close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 18, 0, 0); close.IsCancel = true; panel.Children.Add(close);
        void Refresh() { if (!dialog.Dispatcher.HasShutdownStarted) dialog.Dispatcher.InvokeAsync(view.Refresh); }
        void Theme()
        {
            _palette.Apply(dialog.Resources); dialog.Background = UI.Brush(Surface); dialog.Foreground = UI.Brush(TextPrimary);
            Native.SettingsChrome.SetTheme(dialog, _palette.Dark);
        }
        _app.Updates.Changed += Refresh; PaletteChanged += Theme;
        dialog.Closed += (_, _) => { _app.Updates.Changed -= Refresh; PaletteChanged -= Theme; _updateDialog = null; };
        var frame = new Border { BorderThickness = new Thickness(1), Child = panel };
        frame.SetResourceReference(Border.BorderBrushProperty, "SettingsBorder");
        frame.SetResourceReference(Border.BackgroundProperty, "SettingsSurface"); dialog.Content = frame;
        _updateDialog = dialog; _app.Updates.MarkPromptShown(); dialog.ShowDialog();
    }

    private sealed record UpdateView(FrameworkElement Root, Action Refresh);
    // inline: the About page shows the status and its actions on one settings row, with progress, errors and
    // release notes across the full width below it. The update dialog keeps a card with the actions at the bottom.
    private UpdateView CreateUpdateView(bool inline)
    {
        var updates = _app.Updates;
        var title = TextLine("", inline ? 13 : 15, TextPrimary, FontWeights.SemiBold);
        AutomationProperties.SetLiveSetting(title, AutomationLiveSetting.Polite);
        var detail = TextLine("", 12, TextSecondary); detail.TextWrapping = TextWrapping.Wrap; detail.Margin = new Thickness(0, inline ? 3 : 7, 0, 0);
        var notes = new StackPanel { Margin = new Thickness(0, 0, 10, 0) }; string? notesText = null;
        var noteScroll = new ScrollViewer { Content = notes, MaxHeight = 170, Margin = inline ? new Thickness(0, 2, 0, 16) : new Thickness(0, 16, 0, 0), Style = (Style)FindResource("SettingsScrollViewer") };
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, Margin = inline ? new Thickness(0, 2, 0, 16) : new Thickness(0, 18, 0, 0), Style = (Style)FindResource("SettingsUpdateProgress") };
        AutomationProperties.SetName(progress, "Update download progress");
        var error = TextLine("", 12, _palette.Dark ? "#F0A6AA" : "#B12E3A"); error.Margin = inline ? new Thickness(0, 0, 0, 14) : new Thickness(0, 12, 0, 0); error.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        var primary = ActionButton("Download update", () =>
        {
            if (updates.Stage == AppUpdateStage.Ready) _app.InstallUpdate();
            else if (updates.CanInstall) _ = updates.DownloadAsync(_app.Lifetime.Token);
            else UI.OpenUrl(Updates.GitHubUpdates.RepositoryUrl + "/releases/latest");
        }, true);
        var check = ActionButton("Check for updates", () => { _ = updates.CheckAsync(true, _app.Lifetime.Token); });
        var skip = ActionButton("Skip this version", updates.SkipVersion);
        var cancel = ActionButton("Cancel download", updates.CancelDownload);
        check.Style = cancel.Style = skip.Style = (Style)FindResource("SettingsOutlineButton");
        foreach (var button in new[] { primary, check, cancel, skip }) { button.Padding = new Thickness(14, 6, 14, 6); button.MinWidth = 96; }
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        FrameworkElement root;
        if (inline)
        {
            var header = new Grid { MinHeight = 44 };
            header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 16, 10) };
            labels.Children.Add(title); labels.Children.Add(detail); header.Children.Add(labels);
            // The primary action keeps the right edge; its gap is collapsed together with it.
            primary.Margin = new Thickness(8, 0, 0, 0); skip.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(skip); actions.Children.Add(check); actions.Children.Add(cancel); actions.Children.Add(primary);
            actions.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(actions, 1); header.Children.Add(actions);
            var panel = new StackPanel(); panel.Children.Add(header); panel.Children.Add(progress); panel.Children.Add(error); panel.Children.Add(noteScroll);
            panel.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider) });
            panel.Children.Add(Toggle("Check for updates automatically", updates.AutoCheck, value => updates.AutoCheck = value));
            panel.Children.Add(Toggle("Receive beta updates", updates.Beta, value =>
            {
                updates.Beta = value;
                if (value) _ = updates.CheckAsync(true, _app.Lifetime.Token);
            }, help: "Receives beta updates as well as releases. Betas may be less stable."));
            root = panel;
        }
        else
        {
            var content = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };
            content.Children.Add(title); content.Children.Add(detail); content.Children.Add(noteScroll); content.Children.Add(progress); content.Children.Add(error);
            actions.Margin = new Thickness(0, 18, 0, 0); content.Children.Add(actions);
            primary.MinWidth = 112; primary.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(primary); actions.Children.Add(check); actions.Children.Add(cancel);
            skip.Margin = new Thickness(8, 0, 0, 0); actions.Children.Add(skip);
            var card = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = content };
            card.SetResourceReference(Border.BackgroundProperty, "SettingsInput"); card.SetResourceReference(Border.BorderBrushProperty, "SettingsBorder");
            root = card;
        }
        void Refresh()
        {
            var stage = updates.Stage; var release = updates.Release;
            title.Text = stage switch
            {
                AppUpdateStage.Checking => "Checking for updates…", AppUpdateStage.Downloading => $"Downloading {release?.Version}",
                AppUpdateStage.Ready => "The update is ready", AppUpdateStage.Installing => "Installing the update…",
                _ when release is not null => $"Version {release.Version} is available",
                _ when updates.LastChecked is not null && updates.Error is null && updates.NoticeVersion is null => "BrimDeck is up to date", _ => "Keep BrimDeck up to date"
            };
            detail.Text = stage switch
            {
                AppUpdateStage.Downloading => $"{updates.Progress}% · When the download finishes, BrimDeck can install it and restart.",
                AppUpdateStage.Ready => $"{updates.CurrentVersion} → {release?.Version} · Takes effect after a restart. Your settings are kept.",
                AppUpdateStage.Installing => "BrimDeck is closing and will open again when the update is done.",
                _ when release is not null && updates.IsSkipped(release.Version) => "This version is skipped and will not be announced again. You can still download it.",
                _ when release is not null && !updates.CanInstall => "This copy did not come from the installer or the portable package and cannot update itself. Download the new version from the download page.",
                _ when release is not null => $"{updates.CurrentVersion} → {release.Version} · About {release.Size / 1048576d:0.#} MB",
                _ when updates.LastChecked is { } time => "Last checked " + time.ToLocalTime().ToString("MM-dd HH:mm"),
                _ => updates.AutoCheck ? "BrimDeck checks for a new version in the background after startup and every 6 hours, and when settings open. You can also check at any time."
                    : "Automatic update checks are off. You can check at any time."
            };
            // Release notes laid out as in the What's new window (FillNotes), only as text: remote release content cannot run
            // HTML or scripts. Rebuilt only when the notes change, not on every download progress report.
            var text = release?.Notes.For(Loc.Language) ?? "";
            if (text != notesText) { notesText = text; FillNotes(notes, text); }
            noteScroll.Visibility = text.Length > 0 && stage is not AppUpdateStage.Checking ? Visibility.Visible : Visibility.Collapsed;
            progress.Value = updates.Progress; progress.Visibility = stage == AppUpdateStage.Downloading ? Visibility.Visible : Visibility.Collapsed;
            error.Text = updates.Error ?? ""; error.Visibility = updates.Error is null ? Visibility.Collapsed : Visibility.Visible;
            primary.Content = stage == AppUpdateStage.Ready ? "Install and restart" : updates.CanInstall ? "Download update" : "Open download page";
            primary.Visibility = release is not null && stage != AppUpdateStage.Downloading ? Visibility.Visible : Visibility.Collapsed; primary.IsEnabled = !updates.IsBusy;
            check.IsEnabled = !updates.IsBusy; check.Visibility = stage is AppUpdateStage.Downloading or AppUpdateStage.Ready or AppUpdateStage.Installing ? Visibility.Collapsed : Visibility.Visible;
            // On the About page checking is the main action until a found release hands that role to the download button.
            check.Style = (Style)FindResource(inline && release is null ? "SettingsPrimaryButton" : "SettingsOutlineButton");
            cancel.Visibility = stage == AppUpdateStage.Downloading ? Visibility.Visible : Visibility.Collapsed;
            skip.Visibility = release is not null && stage == AppUpdateStage.Available && !updates.IsSkipped(release.Version) ? Visibility.Visible : Visibility.Collapsed;
            skip.IsEnabled = !updates.IsBusy;
            AutomationProperties.SetName(primary, primary.Content.ToString());
        }
        Refresh(); return new UpdateView(root, Refresh);
    }
}
