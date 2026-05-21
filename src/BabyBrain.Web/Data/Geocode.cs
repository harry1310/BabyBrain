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

    // Great-circle distance between two lat/lng points, in miles (haversine).
    // Used by the search page's distance filter — straight-line, not routed.
    public static double DistanceMiles(double lat1, double lng1, double lat2, double lng2)
    {
        const double earthRadiusMiles = 3958.7613;
        static double ToRadians(double deg) => deg * Math.PI / 180.0;

        var dLat = ToRadians(lat2 - lat1);
        var dLng = ToRadians(lng2 - lng1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2))
              * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return earthRadiusMiles * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
