using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrimDeck.Core;

public enum WeatherKind
{
    None,
    Sunny,
    Rain,
    Thunderstorm,
    Snow,
    Windy,
    Fog,
    Heatwave,
    ClearNight,
    Rainbow
}

public enum WeatherUnit
{
    Celsius,
    Fahrenheit
}

public sealed record WeatherLocation(string Name, string Admin, string Country, double Latitude, double Longitude)
{
    public string DisplayName
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(Name)) parts.Add(Name.Trim());
            if (!string.IsNullOrWhiteSpace(Admin) && !string.Equals(Admin.Trim(), Name.Trim(), StringComparison.OrdinalIgnoreCase))
                parts.Add(Admin.Trim());
            if (!string.IsNullOrWhiteSpace(Country)) parts.Add(Country.Trim());
            return string.Join(", ", parts);
        }
    }
}

public sealed record WeatherHourForecast(DateTime LocalTime, int WmoCode, double TempC, int PrecipProbability);

public sealed record WeatherDayForecast(DateOnly Date, int WmoCode, double TempMaxC, double TempMinC, int PrecipProbability);

public sealed record WeatherSnapshot(
    string LocationName,
    double Latitude,
    double Longitude,
    double TempC,
    double ApparentTempC,
    double WindKmh,
    int HumidityPercent,
    int WmoCode,
    bool IsDay,
    WeatherKind Kind,
    string ConditionText,
    string Quip,
    IReadOnlyList<WeatherHourForecast> Hourly,
    IReadOnlyList<WeatherDayForecast> Daily,
    DateTimeOffset FetchedUtc);

public static class WeatherLogic
{
    public static (WeatherKind Kind, DateTimeOffset? NextRainbowUntilUtc) Classify(
        int wmoCode,
        double tempC,
        double windKmh,
        bool isDay,
        WeatherKind previousKind,
        DateTimeOffset? rainbowUntilUtc,
        DateTimeOffset nowUtc)
    {
        if (wmoCode is >= 95 and <= 99)
            return (WeatherKind.Thunderstorm, null);

        if (wmoCode is (>= 71 and <= 77) or 85 or 86)
            return (WeatherKind.Snow, null);

        if (wmoCode is (>= 51 and <= 67) or (>= 80 and <= 82))
            return (WeatherKind.Rain, null);

        if (wmoCode is 45 or 48)
            return (WeatherKind.Fog, null);

        if (windKmh >= 35)
            return (WeatherKind.Windy, null);

        if (tempC >= 32 && isDay)
            return (WeatherKind.Heatwave, null);

        if (isDay && wmoCode is >= 0 and <= 3)
        {
            if (previousKind is WeatherKind.Rain or WeatherKind.Thunderstorm)
                return (WeatherKind.Rainbow, nowUtc.AddMinutes(15));
            if (rainbowUntilUtc is { } until && nowUtc < until)
                return (WeatherKind.Rainbow, until);
        }

        if (!isDay && wmoCode is >= 0 and <= 3)
            return (WeatherKind.ClearNight, null);

        if (tempC <= 0)
            return (WeatherKind.Snow, null);

        if (isDay && wmoCode is >= 0 and <= 2)
            return (WeatherKind.Sunny, null);

        return (WeatherKind.None, null);
    }

    public static string ConditionLabel(int wmoCode) => wmoCode switch
    {
        0 => "Clear sky",
        1 => "Mainly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Foggy",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 or 63 or 65 => "Rain",
        66 or 67 => "Freezing rain",
        71 or 73 or 75 or 77 => "Snow",
        80 or 81 or 82 => "Rain showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm with hail",
        _ => "Cloudy"
    };

    public static string Quip(WeatherKind kind) => kind switch
    {
        WeatherKind.Sunny => "I'm the only cloud allowed in the sky today.",
        WeatherKind.Rain => "Sorry, couldn't hold it in.",
        WeatherKind.Thunderstorm => "I have feelings and they are 300,000 volts.",
        WeatherKind.Snow => "Brrr… hand-crafting artisanal snowflakes.",
        WeatherKind.Windy => "HOLD ONTO YOUR TASKBAR—",
        WeatherKind.Fog => "Can you even see me? I'm stealth.",
        WeatherKind.Heatwave => "Evaporating… send iced coffee.",
        WeatherKind.ClearNight => "Night shift. Counting satellites.",
        WeatherKind.Rainbow => "Okay fine, I made up for the rain.",
        _ => "Floating by on standby."
    };

    public static NimbusOutfit SuggestedOutfit(WeatherKind kind) => kind switch
    {
        WeatherKind.Sunny => NimbusOutfit.Sunglasses,
        WeatherKind.Snow or WeatherKind.Windy => NimbusOutfit.Scarf,
        _ => NimbusOutfit.None
    };

    public static string FormatTemp(double tempC, WeatherUnit unit, bool includeUnit = true)
    {
        double value = unit == WeatherUnit.Fahrenheit ? tempC * 9.0 / 5.0 + 32.0 : tempC;
        int rounded = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        if (!includeUnit)
            return $"{rounded}°";
        return unit == WeatherUnit.Fahrenheit ? $"{rounded}°F" : $"{rounded}°C";
    }

    public static (WeatherSnapshot Snapshot, DateTimeOffset? NextRainbowUntilUtc) ParseForecastJson(
        string json,
        string locationName,
        double lat,
        double lon,
        WeatherKind previousKind,
        DateTimeOffset? rainbowUntilUtc,
        DateTimeOffset nowUtc)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var current = root.GetProperty("current");
        double tempC = ReadDouble(current, "temperature_2m");
        double apparentC = ReadDouble(current, "apparent_temperature", tempC);
        double windKmh = ReadDouble(current, "wind_speed_10m");
        int humidity = ReadInt(current, "relative_humidity_2m");
        int wmo = ReadInt(current, "weather_code");
        bool isDay = ReadInt(current, "is_day", 1) != 0;
        DateTime currentLocal = current.TryGetProperty("time", out var timeEl) && DateTime.TryParse(timeEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedNow)
            ? parsedNow
            : DateTime.Now;

        var (kind, nextRainbowUntil) = Classify(wmo, tempC, windKmh, isDay, previousKind, rainbowUntilUtc, nowUtc);
        string condition = ConditionLabel(wmo);

        var hourly = new List<WeatherHourForecast>();
        if (root.TryGetProperty("hourly", out var hourlyEl) &&
            hourlyEl.TryGetProperty("time", out var hTimes) &&
            hourlyEl.TryGetProperty("temperature_2m", out var hTemps) &&
            hourlyEl.TryGetProperty("weather_code", out var hCodes))
        {
            hourlyEl.TryGetProperty("precipitation_probability", out var hPrecip);
            var hourCutoff = new DateTime(currentLocal.Year, currentLocal.Month, currentLocal.Day, currentLocal.Hour, 0, 0);
            int count = Math.Min(hTimes.GetArrayLength(), Math.Min(hTemps.GetArrayLength(), hCodes.GetArrayLength()));
            for (int i = 0; i < count && hourly.Count < 6; i++)
            {
                if (!DateTime.TryParse(hTimes[i].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    continue;
                if (dt < hourCutoff) continue;
                double t = hTemps[i].ValueKind == JsonValueKind.Number ? hTemps[i].GetDouble() : tempC;
                int c = hCodes[i].ValueKind == JsonValueKind.Number ? hCodes[i].GetInt32() : wmo;
                int pop = hPrecip.ValueKind == JsonValueKind.Array && i < hPrecip.GetArrayLength() && hPrecip[i].ValueKind == JsonValueKind.Number
                    ? hPrecip[i].GetInt32()
                    : 0;
                hourly.Add(new WeatherHourForecast(dt, c, t, Math.Clamp(pop, 0, 100)));
            }
        }

        var daily = new List<WeatherDayForecast>();
        if (root.TryGetProperty("daily", out var dailyEl) &&
            dailyEl.TryGetProperty("time", out var dTimes) &&
            dailyEl.TryGetProperty("weather_code", out var dCodes) &&
            dailyEl.TryGetProperty("temperature_2m_max", out var dMax) &&
            dailyEl.TryGetProperty("temperature_2m_min", out var dMin))
        {
            dailyEl.TryGetProperty("precipitation_probability_max", out var dPrecip);
            int count = Math.Min(dTimes.GetArrayLength(), Math.Min(dCodes.GetArrayLength(), Math.Min(dMax.GetArrayLength(), dMin.GetArrayLength())));
            for (int i = 0; i < count && daily.Count < 5; i++)
            {
                if (!DateOnly.TryParse(dTimes[i].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    continue;
                int c = dCodes[i].ValueKind == JsonValueKind.Number ? dCodes[i].GetInt32() : wmo;
                double hi = dMax[i].ValueKind == JsonValueKind.Number ? dMax[i].GetDouble() : tempC;
                double lo = dMin[i].ValueKind == JsonValueKind.Number ? dMin[i].GetDouble() : tempC;
                int pop = dPrecip.ValueKind == JsonValueKind.Array && i < dPrecip.GetArrayLength() && dPrecip[i].ValueKind == JsonValueKind.Number
                    ? dPrecip[i].GetInt32()
                    : 0;
                daily.Add(new WeatherDayForecast(date, c, hi, lo, Math.Clamp(pop, 0, 100)));
            }
        }

        var snapshot = new WeatherSnapshot(
            locationName,
            lat,
            lon,
            tempC,
            apparentC,
            windKmh,
            Math.Clamp(humidity, 0, 100),
            wmo,
            isDay,
            kind,
            condition,
            Quip(kind),
            hourly,
            daily,
            nowUtc);
        return (snapshot, nextRainbowUntil);
    }

    public static List<WeatherLocation> ParseGeocodingJson(string json)
    {
        var results = new List<WeatherLocation>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var item in arr.EnumerateArray())
        {
            string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(name)) continue;
            string admin = item.TryGetProperty("admin1", out var a) ? a.GetString() ?? "" : "";
            string country = item.TryGetProperty("country_code", out var cc) ? cc.GetString() ?? ""
                : item.TryGetProperty("country", out var c) ? c.GetString() ?? "" : "";
            if (!item.TryGetProperty("latitude", out var latEl) || !item.TryGetProperty("longitude", out var lonEl))
                continue;
            results.Add(new WeatherLocation(name, admin, country, latEl.GetDouble(), lonEl.GetDouble()));
        }
        return results;
    }

    public static WeatherLocation? ParseIpLocationJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
            return null;

        string city = root.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "";
        string region = root.TryGetProperty("region", out var r) ? r.GetString() ?? "" : "";
        string country = root.TryGetProperty("country_code", out var cc) ? cc.GetString() ?? ""
            : root.TryGetProperty("country", out var co) ? co.GetString() ?? "" : "";

        if (!TryReadCoord(root, "latitude", out double lat) || !TryReadCoord(root, "longitude", out double lon))
            return null;

        if (string.IsNullOrWhiteSpace(city))
            city = !string.IsNullOrWhiteSpace(region) ? region : "Local";

        return new WeatherLocation(city, region, country, lat, lon);
    }

    private static bool TryReadCoord(JsonElement obj, string prop, out double value)
    {
        value = 0;
        if (!obj.TryGetProperty(prop, out var el)) return false;
        if (el.ValueKind == JsonValueKind.Number) { value = el.GetDouble(); return double.IsFinite(value); }
        if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return double.IsFinite(value);
        return false;
    }

    private static double ReadDouble(JsonElement obj, string name, double fallback = 0) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : fallback;

    private static int ReadInt(JsonElement obj, string name, int fallback = 0) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : fallback;
}

public sealed class WeatherService : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(20);
    private readonly string _cachePath;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _timer;
    private bool _disposed;

    private bool _wanted;
    private string _locationName = "";
    private double? _lat;
    private double? _lon;
    private DateTimeOffset? _rainbowUntilUtc;

    public WeatherSnapshot? Current { get; private set; }
    public string? LastError { get; private set; }
    public bool IsLoading { get; private set; }
    public event Action? Changed;

    public WeatherService(string cacheDirectory, HttpClient? httpClient = null)
    {
        _cachePath = Path.Combine(cacheDirectory, "weather-cache.json");
        if (httpClient is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("BrimDeck/1.6");
            _ownsHttp = true;
        }
        else
        {
            _http = httpClient;
        }
        LoadCache();
    }

    public void Apply(DeckSettings settings)
    {
        if (_disposed) return;

        bool wanted = settings.WeatherWanted;
        double? lat = settings.WeatherLatitude;
        double? lon = settings.WeatherLongitude;
        string city = settings.WeatherCity ?? "";

        bool coordsChanged = _lat != lat || _lon != lon;
        _wanted = wanted;
        _lat = lat;
        _lon = lon;
        _locationName = city;

        if (!wanted || lat is null || lon is null)
        {
            _timer?.Dispose();
            _timer = null;
            if (lat is null || lon is null)
            {
                Current = null;
                LastError = null;
                Changed?.Invoke();
            }
            return;
        }

        if (_timer is null)
            _timer = new Timer(_ => _ = RefreshAsync(), null, RefreshInterval, RefreshInterval);

        bool stale = Current is null
            || Math.Abs(Current.Latitude - lat.Value) > 0.01
            || Math.Abs(Current.Longitude - lon.Value) > 0.01
            || DateTimeOffset.UtcNow - Current.FetchedUtc >= RefreshInterval;

        if (coordsChanged || stale)
            _ = RefreshAsync(force: coordsChanged);
    }

    public async Task RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        if (_disposed || !_wanted || _lat is not { } lat || _lon is not { } lon) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed || !_wanted || _lat is not { } curLat || _lon is not { } curLon) return;
            if (!force && Current is { } existing &&
                Math.Abs(existing.Latitude - curLat) <= 0.01 &&
                Math.Abs(existing.Longitude - curLon) <= 0.01 &&
                DateTimeOffset.UtcNow - existing.FetchedUtc < TimeSpan.FromMinutes(2))
            {
                return;
            }

            IsLoading = true;
            LastError = null;
            Changed?.Invoke();

            string url = string.Format(
                CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0:F4}&longitude={1:F4}&current=temperature_2m,apparent_temperature,relative_humidity_2m,is_day,weather_code,wind_speed_10m&hourly=temperature_2m,weather_code,precipitation_probability&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max&forecast_days=5&timezone=auto",
                curLat, curLon);

            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var prevKind = Current?.Kind ?? WeatherKind.None;
            var (snap, nextRainbow) = WeatherLogic.ParseForecastJson(
                json,
                string.IsNullOrWhiteSpace(_locationName) ? $"{curLat:F2}, {curLon:F2}" : _locationName,
                curLat,
                curLon,
                prevKind,
                _rainbowUntilUtc,
                DateTimeOffset.UtcNow);

            _rainbowUntilUtc = nextRainbow;
            Current = snap;
            SaveCache(snap);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
        }
        finally
        {
            IsLoading = false;
            Changed?.Invoke();
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<WeatherLocation>> SearchCitiesAsync(string query, CancellationToken ct = default)
    {
        string trimmed = (query ?? "").Trim();
        if (trimmed.Length < 2) return [];
        string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(trimmed)}&count=6&language=en&format=json";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return WeatherLogic.ParseGeocodingJson(json);
    }

    public async Task<WeatherLocation?> DetectLocationAsync(CancellationToken ct = default)
    {
        foreach (string url in new[] { "https://ipwho.is/", "https://get.geojs.io/v1/ip/geo.json" })
        {
            try
            {
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;
                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (WeatherLogic.ParseIpLocationJson(json) is { } loc)
                    return loc;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Try fallback endpoint.
            }
        }
        return null;
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            string json = File.ReadAllText(_cachePath);
            var snap = JsonSerializer.Deserialize<WeatherSnapshot>(json);
            if (snap is not null)
                Current = snap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Ignore unreadable cache.
        }
    }

    private void SaveCache(WeatherSnapshot snapshot)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignore cache write errors.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _gate.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}
