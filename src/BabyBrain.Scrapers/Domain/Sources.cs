namespace BabyBrain.Scrapers.Domain;

// Human-readable display names for scraper SourceIds. The SourceId itself
// (e.g. "camden_stay_and_play") is a stable machine token stamped on every
// EventOccurrence.Source; this maps it to something a visitor recognises.
//
// Mirrors Categories.Label: a new scraper added without an entry here still
// gets a sensible title-cased fallback, so a missing mapping degrades rather
// than breaks. Labels name the source/organisation, not a single venue —
// several sources (Camden, the library systems) span many venues.
public static class Sources
{
    public static string Label(string sourceId) => sourceId switch
    {
        "bach_to_baby"                   => "Bach to Baby",
        "barbican_parent_and_baby"       => "Barbican Centre",
        "better_talacre_soft_play"       => "Talacre Community Sports Centre",
        "british_museum_family"          => "British Museum",
        "camden_stay_and_play"           => "Camden Stay & Play",
        "city_of_london_libraries"       => "City of London Libraries",
        "design_museum_families"         => "Design Museum",
        "holborn_early_years"            => "Holborn Community Association",
        "islington_findyour"             => "Islington Libraries",
        "lso_under_5s_concerts"          => "LSO St Luke's",
        "ltm_singing_and_story_sessions" => "London Transport Museum",
        "mw_health_classes"              => "Moon Women's Health",
        "postal_museum_post_and_play"    => "The Postal Museum",
        "royal_parks_play_in_the_park"   => "The Royal Parks",
        "southbank_centre_families"      => "Southbank Centre",
        "tockify_fitzrovia"              => "Fitzrovia Community Centre",
        "va_early_years"                 => "Young V&A",
        "wigmore_hall_under_fives"       => "Wigmore Hall",
        "wild_london_family_learning"    => "London Wildlife Trust",
        _ => Humanize(sourceId),
    };

    // Fallback for an unmapped SourceId: "some_new_source" → "Some New Source".
    private static string Humanize(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) return "Unknown source";
        var words = sourceId
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
        return string.Join(' ', words);
    }
}
