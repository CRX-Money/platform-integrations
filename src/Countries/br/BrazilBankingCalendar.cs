namespace CrxMoney.Integrations.Countries.Br;

/// <summary>
/// Brazil's banking calendar: banks book in São Paulo time (UTC−3, no daylight saving since 2019).
/// Every Brazilian source dates its imports with <see cref="StartOfDay"/> of the booking date, so the
/// same movement read from an API, an aggregator or a file lands on the same day.
/// </summary>
public static class BrazilBankingCalendar
{
    /// <summary>The time zone Brazilian banks book in.</summary>
    public static TimeZoneInfo TimeZone { get; } = FindTimeZone();

    /// <summary>The Brazilian calendar date of an instant.</summary>
    public static DateOnly DateOf(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, TimeZone).DateTime);

    /// <summary>Midnight of <paramref name="date"/> in São Paulo: <c>2026-03-10</c> becomes <c>2026-03-10T00:00-03:00</c>.</summary>
    public static DateTimeOffset StartOfDay(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZone.GetUtcOffset(local));
    }

    // IANA ids resolve on Linux, macOS and Windows with ICU; Windows in NLS mode only knows its own id.
    private static TimeZoneInfo FindTimeZone() =>
        TimeZoneInfo.TryFindSystemTimeZoneById("America/Sao_Paulo", out var zone)
            ? zone
            : TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time");
}
