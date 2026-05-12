namespace BabyBrain.Web.Data;

// Cache of postcode → lat/lng resolved via postcodes.io.
// Keyed on the normalised postcode (uppercase, single space) so we look up once
// per real-world location regardless of source-supplied formatting.
public class Geocode
{
    public required string Postcode { get; set; }    // primary key, normalised
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public DateTimeOffset ResolvedAt { get; set; }

    public static string Normalise(string raw)
    {
        var trimmed = raw.Trim().ToUpperInvariant();
        // Collapse internal whitespace; UK postcodes have exactly one space.
        var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts);
    }
}
