namespace AdfXplorer.Core.FileSystems;

/// <summary>
/// Converts AmigaDOS's (days, minutes, ticks)-since-1978-01-01 timestamps to UTC <see cref="DateTime"/>.
/// Shared across filesystem implementations (OFS, and later FFS/SFS/PFS3), which all use this epoch.
/// </summary>
internal static class AmigaTime
{
    private static readonly DateTime Epoch = new(1978, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <param name="ticks">1/50th-of-a-second ticks past the minute.</param>
    public static DateTime ToDateTimeUtc(int days, int minutes, int ticks) =>
        Epoch.AddDays(days).AddMinutes(minutes).AddSeconds(ticks / 50.0);

    public static (int Days, int Minutes, int Ticks) FromDateTimeUtc(DateTime utc)
    {
        var elapsed = utc - Epoch;
        int days = (int)elapsed.TotalDays;
        var remainder = elapsed - TimeSpan.FromDays(days);
        int minutes = (int)remainder.TotalMinutes;
        int ticks = (int)((remainder - TimeSpan.FromMinutes(minutes)).TotalSeconds * 50);
        return (days, minutes, ticks);
    }
}
