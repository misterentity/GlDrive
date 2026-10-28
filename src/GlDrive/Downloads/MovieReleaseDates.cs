using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlDrive.Downloads;

/// <summary>Advisory provider dates. Unknown or malformed fields never prevent a grab.</summary>
[JsonConverter(typeof(MovieReleaseDatesConverter))]
public sealed record MovieReleaseDates(DateOnly? Theatrical = null, DateOnly? Digital = null,
    DateOnly? Physical = null, string Region = "US", string Status = "unknown", DateTimeOffset? CheckedAtUtc = null)
{
    public string ReleaseType => string.Join(" / ", new[]
    {
        Theatrical.HasValue ? "Theatrical" : null, Digital.HasValue ? "Digital" : null, Physical.HasValue ? "Physical" : null
    }.Where(s => s != null)) is { Length: > 0 } types ? types : "Unknown";

    public static string Display(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "Unknown";

    internal static MovieReleaseDates ParseTmdb(string json, string region, DateTimeOffset now)
    {
        DateOnly? theatrical = null, digital = null, physical = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("results", out var countries) || countries.ValueKind != JsonValueKind.Array)
                return new(Region: region, Status: "unavailable", CheckedAtUtc: now);
            foreach (var country in countries.EnumerateArray())
            {
                if (country.ValueKind != JsonValueKind.Object ||
                    !string.Equals(Text(country, "iso_3166_1"), region, StringComparison.OrdinalIgnoreCase) ||
                    !country.TryGetProperty("release_dates", out var releases) || releases.ValueKind != JsonValueKind.Array) continue;
                foreach (var release in releases.EnumerateArray())
                {
                    if (release.ValueKind != JsonValueKind.Object || !release.TryGetProperty("type", out var type) ||
                        type.ValueKind != JsonValueKind.Number || !type.TryGetInt32(out var number) ||
                        Date(Text(release, "release_date")) is not { } date) continue;
                    switch (number)
                    {
                        case 2: case 3: theatrical = Earlier(theatrical, date); break;
                        case 4: digital = Earlier(digital, date); break;
                        case 5: physical = Earlier(physical, date); break;
                    }
                }
            }
        }
        catch (JsonException) { return new(Region: region, Status: "unavailable", CheckedAtUtc: now); }
        return new(theatrical, digital, physical, region,
            theatrical.HasValue || digital.HasValue || physical.HasValue ? "known" : "unknown", now);
    }

    private static DateOnly Earlier(DateOnly? current, DateOnly candidate) => current.HasValue && current.Value < candidate ? current.Value : candidate;
    internal static string? Text(JsonElement node, string property) => node.ValueKind == JsonValueKind.Object &&
        node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static DateOnly? Date(string? text)
    {
        if (text == null || text.Length < 10 || !DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)) return null;
        if (text.Length > 10 && !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return null;
        return date;
    }
}

/// <summary>Optional calendar corruption must not discard the containing wishlist.</summary>
public sealed class MovieReleaseDatesConverter : JsonConverter<MovieReleaseDates>
{
    public override MovieReleaseDates? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var node = document.RootElement;
        if (node.ValueKind != JsonValueKind.Object) return null;
        var region = MovieReleaseDates.Text(node, "region");
        var status = MovieReleaseDates.Text(node, "status");
        return new(MovieReleaseDates.Date(MovieReleaseDates.Text(node, "theatrical")),
            MovieReleaseDates.Date(MovieReleaseDates.Text(node, "digital")), MovieReleaseDates.Date(MovieReleaseDates.Text(node, "physical")),
            region is { Length: 2 } && region.All(char.IsAsciiLetter) ? region.ToUpperInvariant() : "US",
            status is "known" ? status : "unknown", DateTimeOffset.TryParse(MovieReleaseDates.Text(node, "checkedAtUtc"),
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : null);
    }

    public override void Write(Utf8JsonWriter writer, MovieReleaseDates value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteDate(writer, "theatrical", value.Theatrical); WriteDate(writer, "digital", value.Digital); WriteDate(writer, "physical", value.Physical);
        writer.WriteString("region", value.Region); writer.WriteString("status", value.Status);
        if (value.CheckedAtUtc.HasValue) writer.WriteString("checkedAtUtc", value.CheckedAtUtc.Value); else writer.WriteNull("checkedAtUtc");
        writer.WriteEndObject();
    }
    private static void WriteDate(Utf8JsonWriter writer, string name, DateOnly? value)
    {
        if (value.HasValue) writer.WriteString(name, MovieReleaseDates.Display(value)); else writer.WriteNull(name);
    }
}
