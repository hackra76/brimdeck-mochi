using BrimDeck.Core;

namespace BrimDeck.Tests;

internal static class WeatherTests
{
    public static void Run(Action<string, bool> check)
    {
        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

        // 1. Classification across all 9 weather kinds
        var (sunny, _) = WeatherLogic.Classify(0, 24, 10, isDay: true, WeatherKind.None, null, now);
        check("Weather: WMO 0 day 24C is Sunny", sunny == WeatherKind.Sunny);

        var (rain, _) = WeatherLogic.Classify(63, 14, 12, isDay: true, WeatherKind.None, null, now);
        check("Weather: WMO 63 is Rain", rain == WeatherKind.Rain);

        var (storm, _) = WeatherLogic.Classify(95, 18, 25, isDay: true, WeatherKind.None, null, now);
        check("Weather: WMO 95 is Thunderstorm", storm == WeatherKind.Thunderstorm);

        var (snow, _) = WeatherLogic.Classify(73, -3, 8, isDay: true, WeatherKind.None, null, now);
        check("Weather: WMO 73 is Snow", snow == WeatherKind.Snow);

        var (freezeSnow, _) = WeatherLogic.Classify(2, -1, 5, isDay: true, WeatherKind.None, null, now);
        check("Weather: <= 0C clear/cloudy falls back to Snow", freezeSnow == WeatherKind.Snow);

        var (windy, _) = WeatherLogic.Classify(2, 11, 42, isDay: true, WeatherKind.None, null, now);
        check("Weather: Wind >= 35 km/h is Windy", windy == WeatherKind.Windy);

        var (fog, _) = WeatherLogic.Classify(45, 8, 4, isDay: true, WeatherKind.None, null, now);
        check("Weather: WMO 45 is Fog", fog == WeatherKind.Fog);

        var (heat, _) = WeatherLogic.Classify(0, 35, 6, isDay: true, WeatherKind.None, null, now);
        check("Weather: >= 32C day is Heatwave", heat == WeatherKind.Heatwave);

        var (night, _) = WeatherLogic.Classify(0, 12, 5, isDay: false, WeatherKind.None, null, now);
        check("Weather: WMO 0 at night is ClearNight", night == WeatherKind.ClearNight);

        // Post-rain rainbow transition and expiry
        var (rainbow, until) = WeatherLogic.Classify(1, 19, 8, isDay: true, WeatherKind.Rain, null, now);
        check("Weather: Rain -> Clear starts 15m Rainbow", rainbow == WeatherKind.Rainbow && until == now.AddMinutes(15));

        var (stillRainbow, _) = WeatherLogic.Classify(1, 19, 8, isDay: true, WeatherKind.Rainbow, until, now.AddMinutes(10));
        check("Weather: Rainbow persists within 15m window", stillRainbow == WeatherKind.Rainbow);

        var (expiredRainbow, nextUntil) = WeatherLogic.Classify(1, 19, 8, isDay: true, WeatherKind.Rainbow, until, now.AddMinutes(16));
        check("Weather: Rainbow expires after 15m into Sunny", expiredRainbow == WeatherKind.Sunny && nextUntil is null);

        // 2. Suggested outfits
        check("Weather outfit: Sunny -> Sunglasses", WeatherLogic.SuggestedOutfit(WeatherKind.Sunny) == NimbusOutfit.Sunglasses);
        check("Weather outfit: Snow -> Scarf", WeatherLogic.SuggestedOutfit(WeatherKind.Snow) == NimbusOutfit.Scarf);
        check("Weather outfit: Windy -> Scarf", WeatherLogic.SuggestedOutfit(WeatherKind.Windy) == NimbusOutfit.Scarf);
        check("Weather outfit: Rain -> None", WeatherLogic.SuggestedOutfit(WeatherKind.Rain) == NimbusOutfit.None);

        // 3. Temperature formatting
        check("Weather temp: 20C in Celsius", WeatherLogic.FormatTemp(20.4, WeatherUnit.Celsius) == "20°C");
        check("Weather temp: 20C in Fahrenheit", WeatherLogic.FormatTemp(20.0, WeatherUnit.Fahrenheit) == "68°F");
        check("Weather temp: short format omits unit", WeatherLogic.FormatTemp(-3.2, WeatherUnit.Celsius, includeUnit: false) == "-3°");

        // 4. Open-Meteo forecast JSON parsing
        const string forecastJson = """
        {
          "current": {
            "time": "2026-07-15T12:15",
            "temperature_2m": 24.6,
            "apparent_temperature": 25.1,
            "relative_humidity_2m": 52,
            "is_day": 1,
            "weather_code": 0,
            "wind_speed_10m": 12.4
          },
          "hourly": {
            "time": ["2026-07-15T11:00", "2026-07-15T12:00", "2026-07-15T13:00", "2026-07-15T14:00"],
            "temperature_2m": [22.0, 24.6, 26.1, 27.0],
            "weather_code": [1, 0, 0, 2],
            "precipitation_probability": [5, 0, 0, 10]
          },
          "daily": {
            "time": ["2026-07-15", "2026-07-16"],
            "weather_code": [0, 61],
            "temperature_2m_max": [28.0, 21.5],
            "temperature_2m_min": [16.0, 14.0],
            "precipitation_probability_max": [10, 80]
          }
        }
        """;
        var (snap, _) = WeatherLogic.ParseForecastJson(forecastJson, "Bratislava, SK", 48.15, 17.11, WeatherKind.None, null, now);
        check("Weather parse: snapshot fields match", snap.LocationName == "Bratislava, SK" && snap.Kind == WeatherKind.Sunny && snap.HumidityPercent == 52);
        check("Weather parse: hourly skips past hours and keeps upcoming", snap.Hourly.Count == 3 && snap.Hourly[0].LocalTime.Hour == 12);
        check("Weather parse: daily forecast parsed", snap.Daily.Count == 2 && snap.Daily[1].WmoCode == 61 && snap.Daily[1].PrecipProbability == 80);

        // 5. Geocoding JSON parsing
        const string geoJson = """
        {
          "results": [
            { "name": "Bratislava", "admin1": "Bratislava", "country_code": "SK", "latitude": 48.1486, "longitude": 17.1077 },
            { "name": "Vienna", "admin1": "Vienna", "country_code": "AT", "latitude": 48.2085, "longitude": 16.3721 }
          ]
        }
        """;
        var cities = WeatherLogic.ParseGeocodingJson(geoJson);
        check("Weather geocode: parses cities and dedupes identical admin", cities.Count == 2 && cities[0].DisplayName == "Bratislava, SK");

        // 6. Settings, DeckPages & CompactParts integration
        var settings = new DeckSettings
        {
            WeatherCity = "  Bratislava, SK  ",
            WeatherLatitude = 48.15,
            WeatherLongitude = 17.11,
            WeatherWidth = 9999,
            WeatherHeight = 50,
            NotchWeather = true,
            CapsuleWeather = false
        };
        settings.Normalize();
        check("Weather settings: clamps size and trims city", settings.WeatherWidth == DeckPages.MaxWidth && settings.WeatherHeight == DeckPages.MinHeight && settings.WeatherCity == "Bratislava, SK");
        check("Weather settings: ShowsWeather respects style", settings.ShowsWeather(CompactStyle.Notch) && !settings.ShowsWeather(CompactStyle.Capsule) && !settings.ShowsWeather(CompactStyle.Line));
        check("Weather settings: WeatherWanted true with coordinates", settings.WeatherWanted);
        check("Weather settings: DeckPages includes Weather", DeckPages.Order.Contains(DeckPage.Weather) && DeckPages.Shown(settings, DeckPage.Weather));
        check("Weather settings: CompactParts.Default includes Weather", CompactParts.Default.Contains(CompactPart.Weather));
    }
}
