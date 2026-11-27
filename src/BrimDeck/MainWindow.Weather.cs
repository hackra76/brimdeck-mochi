using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;
using BrimDeck.Native;
using Path = System.Windows.Shapes.Path;

namespace BrimDeck;

public partial class MainWindow
{
    private FrameworkElement? _compactWeather;

    private FrameworkElement? WeatherBadge(double scale)
    {
        _compactWeather = null;
        if (!Settings.ShowsWeather(EffectiveStyle) || _app.Weather.Current is not { } wx)
            return null;

        double s = CompactMusicLayout.SafeScale(scale);
        double iconSize = CompactMusicLayout.Whole(14 * s);
        double gap = CompactMusicLayout.Whole(3 * s);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double start = DevicePixels.Up(iconSize + gap, dpi);

        string tempText = WeatherLogic.FormatTemp(wx.TempC, Settings.WeatherUnit, includeUnit: false);
        var tempBlock = new TextBlock
        {
            Text = tempText,
            FontFamily = UI.PanelFont,
            FontSize = CompactMusicLayout.Half(11.5 * s),
            FontWeight = FontWeights.SemiBold,
            Foreground = UI.Brush(UI.Primary),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(tempBlock, FontNumeralAlignment.Tabular);
        tempBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textW = tempBlock.DesiredSize.Width;
        tempBlock.Margin = new Thickness(start, 0, 0, 0);

        var badge = new Grid
        {
            Width = DevicePixels.Up(start + textW, dpi),
            Height = CompactMusicLayout.Whole(16 * s),
            Background = Brushes.Transparent
        };

        var icon = WeatherVectorIcon(wx.WmoCode, wx.IsDay, iconSize);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.VerticalAlignment = VerticalAlignment.Center;

        badge.Children.Add(icon);
        badge.Children.Add(tempBlock);

        string fullTemp = WeatherLogic.FormatTemp(wx.TempC, Settings.WeatherUnit);
        string tip = $"{wx.LocationName} · {fullTemp} · {wx.ConditionText}\n“{wx.Quip}”";
        badge.ToolTip = tip;
        AutomationProperties.SetName(badge, $"{wx.LocationName} {fullTemp} {wx.ConditionText}");
        _compactWeather = badge;
        return badge;
    }

    private bool PointerOnWeather()
    {
        if (_compactWeather is not { IsVisible: true } badge || PresentationSource.FromVisual(badge) is null)
            return false;
        var rect = new Rect(badge.PointToScreen(new Point()), badge.PointToScreen(new Point(badge.ActualWidth, badge.ActualHeight)));
        rect.Inflate(4, 6);
        return rect.Contains(WindowsHost.Cursor());
    }

    private void RenderWeather()
    {
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed;
        MusicContent.Visibility = Visibility.Collapsed;
        MonitorContent.Visibility = Visibility.Collapsed;
        ShelfContent.Visibility = Visibility.Collapsed;
        WeatherContent.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = Visibility.Collapsed;
        ApplicationPages.Visibility = Visibility.Collapsed;
        ClockLabel.Visibility = Visibility.Visible;

        double width = ContentCanvas.Width, height = ContentCanvas.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;

        var header = PanelHeader.For(ToolbarFor(height), height < 170 ? 14 : 20);
        ApplyHeader(Settings.UsagePage ? header.WithControlsOf(CachedUsageHeader()) : header);

        WeatherContent.Margin = new Thickness(_header.Side, 0, _header.Side, height < 170 ? 8 : 12);
        WeatherContent.Children.Clear();

        if (Settings.WeatherLatitude is null || Settings.WeatherLongitude is null)
        {
            WeatherContent.Children.Add(BuildWeatherEmptyState());
            return;
        }

        if (_app.Weather.Current is not { } wx)
        {
            WeatherContent.Children.Add(BuildWeatherLoadingState());
            return;
        }

        bool wide = width >= 560;
        bool compactH = height < 185;

        var root = new Grid();
        if (wide)
        {
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        }
        else
        {
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        // Left / Top Hero Card
        var heroCard = new Border
        {
            Background = UI.Brush(PanelTheme.Hex("fill")),
            BorderBrush = UI.Brush(PanelTheme.Hex("track")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(PanelTheme.Corner(10)),
            Padding = new Thickness(compactH ? 10 : 12, compactH ? 8 : 10, compactH ? 10 : 12, compactH ? 8 : 10)
        };

        var heroStack = new Grid();
        heroStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        heroStack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        heroStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Top bar: Location + Refresh
        var topRow = new DockPanel();
        var refreshBtn = new Button
        {
            Content = _app.Weather.IsLoading ? "…" : "↻",
            Style = (Style)FindResource("PanelGlyphButton"),
            Width = 22,
            Height = 22,
            FontSize = 12,
            ToolTip = "Refresh weather"
        };
        refreshBtn.Click += (_, _) => _ = _app.Weather.RefreshAsync(force: true);
        System.Windows.Controls.DockPanel.SetDock(refreshBtn, System.Windows.Controls.Dock.Right);
        topRow.Children.Add(refreshBtn);

        var cityText = new TextBlock
        {
            Text = wx.LocationName,
            FontFamily = UI.PanelFont,
            FontSize = compactH ? 12 : 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = UI.Brush(UI.Primary),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        topRow.Children.Add(cityText);
        heroStack.Children.Add(topRow);

        // Middle: Icon + Temp + Condition + Stats
        var midRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, compactH ? 4 : 6, 0, compactH ? 4 : 6)
        };
        var bigIcon = WeatherVectorIcon(wx.WmoCode, wx.IsDay, compactH ? 28 : 34);
        bigIcon.VerticalAlignment = VerticalAlignment.Center;
        bigIcon.Margin = new Thickness(0, 0, 10, 0);
        midRow.Children.Add(bigIcon);

        var tempVal = new TextBlock
        {
            Text = WeatherLogic.FormatTemp(wx.TempC, Settings.WeatherUnit),
            FontFamily = UI.PanelFont,
            FontSize = compactH ? 24 : 28,
            FontWeight = FontWeights.SemiBold,
            Foreground = UI.Brush(UI.Primary),
            VerticalAlignment = VerticalAlignment.Center
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(tempVal, FontNumeralAlignment.Tabular);
        midRow.Children.Add(tempVal);

        var detailStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        detailStack.Children.Add(new TextBlock
        {
            Text = wx.ConditionText,
            FontFamily = UI.PanelFont,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = UI.Brush(UI.Primary)
        });
        detailStack.Children.Add(new TextBlock
        {
            Text = $"Feels {WeatherLogic.FormatTemp(wx.ApparentTempC, Settings.WeatherUnit, false)} · Wind {Math.Round(wx.WindKmh)} km/h · {wx.HumidityPercent}%",
            FontFamily = UI.PanelFont,
            FontSize = 10.5,
            Foreground = UI.Brush(UI.Secondary),
            Margin = new Thickness(0, 1, 0, 0)
        });
        midRow.Children.Add(detailStack);
        Grid.SetRow(midRow, 1);
        heroStack.Children.Add(midRow);

        // Quip footer
        var quip = new TextBlock
        {
            Text = $"“{wx.Quip}”",
            FontFamily = UI.PanelFont,
            FontSize = 11,
            FontStyle = FontStyles.Italic,
            Foreground = UI.Brush(UI.Secondary),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(quip, 2);
        heroStack.Children.Add(quip);

        heroCard.Child = heroStack;
        root.Children.Add(heroCard);

        // Right / Bottom Forecast Panel
        var forecastGrid = new Grid();
        if (wide && !compactH)
        {
            forecastGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            forecastGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            forecastGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }
        else
        {
            forecastGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        // Hourly strip
        if (wx.Hourly.Count > 0 && (!compactH || !wide))
        {
            var hourCard = new Border
            {
                Background = UI.Brush(PanelTheme.Hex("fill")),
                BorderBrush = UI.Brush(PanelTheme.Hex("track")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(PanelTheme.Corner(10)),
                Padding = new Thickness(8, 6, 8, 6)
            };
            var hourRow = new UniformGridCompat(Math.Min(6, wx.Hourly.Count));
            foreach (var h in wx.Hourly.Take(6))
            {
                var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                col.Children.Add(new TextBlock
                {
                    Text = h.LocalTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                    FontFamily = UI.PanelFont,
                    FontSize = 10,
                    Foreground = UI.Brush(UI.Secondary),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                var ic = WeatherVectorIcon(h.WmoCode, h.LocalTime.Hour is >= 6 and < 20, 15);
                ic.Margin = new Thickness(0, 3, 0, 3);
                ic.HorizontalAlignment = HorizontalAlignment.Center;
                col.Children.Add(ic);
                col.Children.Add(new TextBlock
                {
                    Text = WeatherLogic.FormatTemp(h.TempC, Settings.WeatherUnit, false),
                    FontFamily = UI.PanelFont,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = UI.Brush(UI.Primary),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                hourRow.Children.Add(col);
            }
            hourCard.Child = hourRow;
            forecastGrid.Children.Add(hourCard);
        }

        // Daily strip
        if (wx.Daily.Count > 0)
        {
            var dayCard = new Border
            {
                Background = UI.Brush(PanelTheme.Hex("fill")),
                BorderBrush = UI.Brush(PanelTheme.Hex("track")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(PanelTheme.Corner(10)),
                Padding = new Thickness(10, 6, 10, 6)
            };
            var dayList = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            int maxDays = compactH ? 3 : 5;
            var today = DateOnly.FromDateTime(DateTime.Now);
            foreach (var d in wx.Daily.Take(maxDays))
            {
                var line = new Grid { Margin = new Thickness(0, 1.5, 0, 1.5) };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                string dayName = d.Date == today ? "Today" : d.Date.ToString("ddd", CultureInfo.InvariantCulture);
                var nameTb = new TextBlock
                {
                    Text = dayName,
                    FontFamily = UI.PanelFont,
                    FontSize = 11,
                    Foreground = UI.Brush(d.Date == today ? UI.Primary : UI.Secondary),
                    FontWeight = d.Date == today ? FontWeights.SemiBold : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center
                };
                line.Children.Add(nameTb);

                var ic = WeatherVectorIcon(d.WmoCode, true, 14);
                ic.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(ic, 1);
                line.Children.Add(ic);

                if (d.PrecipProbability >= 15)
                {
                    var popTb = new TextBlock
                    {
                        Text = $"{d.PrecipProbability}%",
                        FontFamily = UI.PanelFont,
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Color.FromRgb(96, 165, 250)),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(popTb, 2);
                    line.Children.Add(popTb);
                }

                var rangeTb = new TextBlock
                {
                    Text = $"{WeatherLogic.FormatTemp(d.TempMinC, Settings.WeatherUnit, false)} / {WeatherLogic.FormatTemp(d.TempMaxC, Settings.WeatherUnit, false)}",
                    FontFamily = UI.PanelFont,
                    FontSize = 11,
                    FontWeight = FontWeights.Medium,
                    Foreground = UI.Brush(UI.Primary),
                    VerticalAlignment = VerticalAlignment.Center
                };
                System.Windows.Documents.Typography.SetNumeralAlignment(rangeTb, FontNumeralAlignment.Tabular);
                Grid.SetColumn(rangeTb, 3);
                line.Children.Add(rangeTb);

                dayList.Children.Add(line);
            }
            dayCard.Child = dayList;
            if (forecastGrid.RowDefinitions.Count > 1) Grid.SetRow(dayCard, 2);
            forecastGrid.Children.Add(dayCard);
        }

        if (wide) Grid.SetColumn(forecastGrid, 2);
        else Grid.SetRow(forecastGrid, 2);
        root.Children.Add(forecastGrid);

        WeatherContent.Children.Add(root);
    }

    private FrameworkElement BuildWeatherEmptyState()
    {
        var box = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        box.Children.Add(new TextBlock
        {
            Text = "No weather location set",
            FontFamily = UI.PanelFont,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = UI.Brush(UI.Primary),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        box.Children.Add(new TextBlock
        {
            Text = "Set your city so Nimbus can react to local weather and show forecasts.",
            FontFamily = UI.PanelFont,
            FontSize = 11.5,
            Foreground = UI.Brush(UI.Secondary),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 10)
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var detectBtn = new Button
        {
            Content = "Detect my city",
            Style = (Style)FindResource("PanelSourceButton"),
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 8, 0)
        };
        detectBtn.Click += async (_, _) =>
        {
            detectBtn.IsEnabled = false;
            detectBtn.Content = "Detecting…";
            var loc = await _app.Weather.DetectLocationAsync();
            if (loc is not null)
            {
                var next = Settings.Copy();
                next.WeatherCity = loc.DisplayName;
                next.WeatherLatitude = loc.Latitude;
                next.WeatherLongitude = loc.Longitude;
                _app.UpdateSettings(next);
            }
            else
            {
                detectBtn.IsEnabled = true;
                detectBtn.Content = "Detect my city";
            }
        };
        actions.Children.Add(detectBtn);

        var settingsBtn = new Button
        {
            Content = "Open Weather settings",
            Style = (Style)FindResource("PanelSourceButton"),
            Padding = new Thickness(12, 5, 12, 5)
        };
        settingsBtn.Click += (_, _) => _app.OpenSettings();
        actions.Children.Add(settingsBtn);

        box.Children.Add(actions);
        return box;
    }

    private FrameworkElement BuildWeatherLoadingState()
    {
        var box = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        box.Children.Add(new TextBlock
        {
            Text = _app.Weather.LastError is { Length: > 0 } err
                ? $"Could not load weather: {err}"
                : $"Loading weather for {Settings.WeatherCity}…",
            FontFamily = UI.PanelFont,
            FontSize = 12,
            Foreground = UI.Brush(UI.Secondary),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        if (_app.Weather.LastError is { Length: > 0 })
        {
            var retry = new Button
            {
                Content = "Retry",
                Style = (Style)FindResource("PanelSourceButton"),
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            retry.Click += (_, _) => _ = _app.Weather.RefreshAsync(force: true);
            box.Children.Add(retry);
        }
        return box;
    }

    private static FrameworkElement WeatherVectorIcon(int wmoCode, bool isDay, double size)
    {
        string pathData;
        Color color;
        if (wmoCode is >= 95 and <= 99)
        {
            pathData = "M 6,11 H 15 A 3,3 0 0 0 15.2,5.2 A 4.2,4.2 0 0 0 7,4.8 A 3.2,3.2 0 0 0 6,11 Z M 11,11 L 9,15 H 12 L 10,19";
            color = Color.FromRgb(250, 204, 21);
        }
        else if (wmoCode is (>= 71 and <= 77) or 85 or 86)
        {
            pathData = "M 6,11 H 15 A 3,3 0 0 0 15.2,5.2 A 4.2,4.2 0 0 0 7,4.8 A 3.2,3.2 0 0 0 6,11 Z M 7.5,14.5 V 16.5 M 11,14.5 V 16.5 M 14.5,14.5 V 16.5";
            color = Color.FromRgb(186, 230, 253);
        }
        else if (wmoCode is (>= 51 and <= 67) or (>= 80 and <= 82))
        {
            pathData = "M 6,11 H 15 A 3,3 0 0 0 15.2,5.2 A 4.2,4.2 0 0 0 7,4.8 A 3.2,3.2 0 0 0 6,11 Z M 8,14 L 7,17 M 11.5,14 L 10.5,17 M 15,14 L 14,17";
            color = Color.FromRgb(96, 165, 250);
        }
        else if (wmoCode is 45 or 48)
        {
            pathData = "M 4,7 H 16 M 3,11 H 17 M 5,15 H 15";
            color = Color.FromRgb(203, 213, 225);
        }
        else if (wmoCode is 0 or 1)
        {
            if (isDay)
            {
                pathData = "M 10,6 A 4,4 0 1 1 9.99,6 Z M 10,2 V 3.5 M 10,16.5 V 18 M 2,10 H 3.5 M 16.5,10 H 18 M 4.3,4.3 L 5.4,5.4 M 14.6,14.6 L 15.7,15.7 M 15.7,4.3 L 14.6,5.4 M 5.4,14.6 L 4.3,15.7";
                color = Color.FromRgb(251, 191, 36);
            }
            else
            {
                pathData = "M 13.5,14.5 A 5.5,5.5 0 0 1 7.5,5 A 5.5,5.5 0 1 0 15,12.5 A 5.5,5.5 0 0 1 13.5,14.5 Z";
                color = Color.FromRgb(165, 180, 252);
            }
        }
        else
        {
            pathData = "M 5.5,14 H 15 A 3,3 0 0 0 15.2,8.2 A 4.5,4.5 0 0 0 6.5,7.6 A 3.4,3.4 0 0 0 5.5,14 Z";
            color = Color.FromRgb(203, 213, 225);
        }

        var path = new Path
        {
            Data = Geometry.Parse(pathData),
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        return new Viewbox
        {
            Width = size,
            Height = size,
            Child = new Canvas { Width = 20, Height = 20, Children = { path } }
        };
    }

    private sealed class UniformGridCompat : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGridCompat(int columns) => Columns = Math.Max(1, columns);
    }
}
