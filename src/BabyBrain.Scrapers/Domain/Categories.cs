namespace BabyBrain.Scrapers.Domain;

// Flat list of event categories. Each scraper picks one and stamps it on every
// row it emits. Adding a new category is just a new constant — no migration,
// no enum drama. Keep these short lowercase tokens; display labels live in the
// UI.
public static class Categories
{
    public const string Community = "community";
    public const string Library = "library";
    public const string Museum = "museum";
    public const string Gallery = "gallery";
    public const string Concert = "concert";
    public const string Cinema = "cinema";
    public const string Class = "class";
    public const string Outdoors = "outdoors";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Community, Library, Museum, Gallery, Concert, Cinema, Class, Outdoors,
    };

    // Human-readable label for the UI. Falls back to title-cased category if
    // a new category is added but the label hasn't been wired up.
    public static string Label(string category) => category switch
    {
        Community => "Community",
        Library => "Library",
        Museum => "Museum",
        Gallery => "Gallery",
        Concert => "Concert",
        Cinema => "Cinema",
        Class => "Class",
        Outdoors => "Outdoors",
        _ => string.IsNullOrEmpty(category) ? "Other" : char.ToUpper(category[0]) + category[1..],
    };

    // Hex colour used for card accents and map pins. Picked to be distinct and
    // reasonably accessible against white. Unknown categories fall back to grey.
    public static string Color(string category) => category switch
    {
        Community => "#0d6efd", // blue
        Library => "#198754",   // green
        Museum => "#6f42c1",    // purple
        Gallery => "#d63384",   // pink
        Concert => "#fd7e14",   // orange
        Cinema => "#dc3545",    // red
        Class => "#20c997",     // teal
        Outdoors => "#74b816",  // lime
        _ => "#6c757d",         // grey
    };
}
