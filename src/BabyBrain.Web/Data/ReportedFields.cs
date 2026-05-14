namespace BabyBrain.Web.Data;

// What a reporter says is wrong with an event. Token strings go in
// EventOccurrence.ReportedField; the Label / Validate helpers convert
// to/from the display text and the validated set.
//
// Keep tokens stable — they're the wire format for /api/report-event AND
// the persisted value in SQLite. Display labels are mutable.
public static class ReportedFields
{
    public const string AgeRange = "age_range";
    public const string Time = "time";
    public const string Date = "date";
    public const string Location = "location";
    public const string Cost = "cost";
    public const string Title = "title";
    public const string NotRunning = "not_running";

    public static readonly IReadOnlyList<string> All = new[]
    {
        AgeRange, Time, Date, Location, Cost, Title, NotRunning,
    };

    public static string Label(string? token) => token switch
    {
        AgeRange => "Age range",
        Time => "Time",
        Date => "Date",
        Location => "Location",
        Cost => "Cost",
        Title => "Title",
        NotRunning => "Event isn't running anymore",
        _ => string.IsNullOrEmpty(token) ? "Unknown" : token,
    };

    public static bool IsValid(string? token) => token is not null && All.Contains(token);
}
