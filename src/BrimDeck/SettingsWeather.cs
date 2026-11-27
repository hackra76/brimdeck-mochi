using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    internal const int WeatherSettingsPage = 17;

    private void WeatherSettings()
    {
        SectionTitle("Location & Units");
        var unitStrip = new UniformGrid { Columns = 2, Width = 140 };
        foreach (var (unitVal, unitLabel) in new[] { (WeatherUnit.Celsius, "°C"), (WeatherUnit.Fahrenheit, "°F") })
        {
            var opt = new RadioButton
            {
                Style = (Style)FindResource("SettingsSegment"),
                Content = unitLabel,
                GroupName = "WeatherUnit",
                IsChecked = S.WeatherUnit == unitVal
            };
            AutomationProperties.SetName(opt, "Temperature unit " + unitLabel);
            opt.Checked += (_, _) => Change(s => s.WeatherUnit = unitVal);
            unitStrip.Children.Add(opt);
        }

        var cityBox = new TextBox
        {
            Width = 190,
            Text = S.WeatherCity,
            Foreground = UI.Brush(TextPrimary),
            Background = UI.Brush(_palette.Input),
            BorderBrush = UI.Brush(_palette.InputBorder),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 8, 5),
            FontFamily = Fonts.PlainOf(UiFontRole.Panel),
            FontSize = 12.5,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(cityBox, "Weather city");

        var resultsPanel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 2) };
        var weatherStatus = TextLine("", 12, TextSecondary);

        void UpdateWeatherStatusText()
        {
            if (S.WeatherLatitude is null || S.WeatherLongitude is null)
            {
                weatherStatus.Text = "No location set — search a city or click Detect.";
                return;
            }
            if (_app.Weather.Current is { } wx)
            {
                string temp = WeatherLogic.FormatTemp(wx.TempC, S.WeatherUnit);
                weatherStatus.Text = $"{wx.LocationName} · {temp} · {wx.ConditionText} — “{wx.Quip}”";
            }
            else if (_app.Weather.IsLoading)
            {
                weatherStatus.Text = $"Loading weather for {S.WeatherCity}…";
            }
            else if (_app.Weather.LastError is { Length: > 0 } err)
            {
                weatherStatus.Text = $"Could not fetch weather: {err}";
            }
            else
            {
                weatherStatus.Text = S.WeatherCity;
            }
        }
        UpdateWeatherStatusText();

        void PickLocation(WeatherLocation loc)
        {
            cityBox.Text = loc.DisplayName;
            resultsPanel.Children.Clear();
            resultsPanel.Visibility = Visibility.Collapsed;
            Change(s =>
            {
                s.WeatherCity = loc.DisplayName;
                s.WeatherLatitude = loc.Latitude;
                s.WeatherLongitude = loc.Longitude;
            });
            UpdateWeatherStatusText();
        }

        Button searchBtn = null!;
        async Task RunSearchAsync()
        {
            string q = cityBox.Text.Trim();
            if (q.Length < 2) return;
            searchBtn.IsEnabled = false;
            searchBtn.Content = "…";
            try
            {
                var found = await _app.Weather.SearchCitiesAsync(q);
                resultsPanel.Children.Clear();
                if (found.Count == 0)
                {
                    resultsPanel.Children.Add(TextLine("No matching cities found.", 11.5, TextTertiary));
                }
                else
                {
                    foreach (var loc in found)
                    {
                        var itemBtn = ActionButton(loc.DisplayName, () => PickLocation(loc));
                        itemBtn.HorizontalAlignment = HorizontalAlignment.Left;
                        itemBtn.Margin = new Thickness(0, 2, 0, 2);
                        resultsPanel.Children.Add(itemBtn);
                    }
                }
                resultsPanel.Visibility = Visibility.Visible;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                weatherStatus.Text = $"Search failed: {ex.Message}";
            }
            finally
            {
                searchBtn.IsEnabled = true;
                searchBtn.Content = "Search";
            }
        }

        searchBtn = ActionButton("Search", () => _ = RunSearchAsync());
        searchBtn.Margin = new Thickness(6, 0, 0, 0);
        cityBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _ = RunSearchAsync(); e.Handled = true; } };

        Button detectBtn = null!;
        detectBtn = ActionButton("Detect", async () =>
        {
            detectBtn.IsEnabled = false;
            detectBtn.Content = "…";
            try
            {
                var loc = await _app.Weather.DetectLocationAsync();
                if (loc is not null) PickLocation(loc);
                else weatherStatus.Text = "Could not detect your city automatically. Try searching by name.";
            }
            finally
            {
                detectBtn.IsEnabled = true;
                detectBtn.Content = "Detect";
            }
        });
        detectBtn.Margin = new Thickness(6, 0, 0, 0);

        var inputBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        inputBar.Children.Add(cityBox);
        inputBar.Children.Add(searchBtn);
        inputBar.Children.Add(detectBtn);

        var locationContainer = new StackPanel();
        locationContainer.Children.Add(Row("Location", inputBar,
            help: () => "Uses Open-Meteo (free, no API key). Pick a city or click Detect for one-shot IP geolocation."));
        locationContainer.Children.Add(resultsPanel);

        Group(
            locationContainer,
            Row("Temperature unit", SegmentBorder(unitStrip)),
            Row("Current weather", weatherStatus)
        );

        SectionTitle("Display & Nimbus Reactions");
        Group(
            Toggle("Nimbus reacts to local weather", S.NimbusWeatherEnabled,
                value => Change(s => s.NimbusWeatherEnabled = value),
                help: "Nimbus changes mood, body tint, and weather props (sun, rain, snow, storm, wind, fog, night, rainbow) when idle or sleeping."),
            Toggle("Show weather in collapsed notch", S.NotchWeather,
                value => Change(s => s.NotchWeather = value)),
            Toggle("Show weather in collapsed capsule", S.CapsuleWeather,
                value => Change(s => s.CapsuleWeather = value)),
            Toggle("Show weather page in expanded panel", S.WeatherPage,
                value => Change(s => s.WeatherPage = value))
        );
    }
}
