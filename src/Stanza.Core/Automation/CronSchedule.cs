using System.Globalization;

namespace Stanza.Core.Automation;

/// <summary>Five-field cron. Day-of-month and weekday restrictions are ORed. Invalid local
/// times are skipped; ambiguous local times run once, at the earlier UTC instant.</summary>
public sealed class CronSchedule
{
    private readonly HashSet<int>[] fields;
    private readonly bool anyDay, anyWeekday;
    public CronSchedule(string expression)
    {
        if (expression is null || expression.Length > 200) throw new ArgumentException("Supply a five-field cron expression.");
        var parts = expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) throw new ArgumentException("Cron requires minute hour day-of-month month weekday.");
        fields = [Parse(parts[0], 0, 59), Parse(parts[1], 0, 23), Parse(parts[2], 1, 31),
            Parse(parts[3], 1, 12), Parse(parts[4], 0, 7)];
        if (fields[4].Remove(7)) fields[4].Add(0);
        anyDay = parts[2].StartsWith('*'); anyWeekday = parts[4].StartsWith('*');
    }

    public DateTimeOffset Next(DateTimeOffset after, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var start = TimeZoneInfo.ConvertTime(after, zone).Date;
        // Covers the Gregorian leap-year cycle needed for Feb 29, including non-leap centuries.
        var end = start.AddYears(8);
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            if (!fields[3].Contains(day.Month)) continue;
            var dom = fields[2].Contains(day.Day); var dow = fields[4].Contains((int)day.DayOfWeek);
            // Vixie semantics: when either field starts with '*', both selected value sets
            // must match (including */n); otherwise restricted DOM and DOW are alternatives.
            if (!(anyDay || anyWeekday ? dom && dow : dom || dow)) continue;
            foreach (var hour in fields[1].Order())
            foreach (var minute in fields[0].Order())
            {
                var local = DateTime.SpecifyKind(day.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
                if (zone.IsInvalidTime(local)) continue;
                var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
                var utc = new DateTimeOffset(local, offset).ToUniversalTime();
                if (utc > after) return utc;
            }
        }
        throw new ArgumentException("Cron has no occurrence within eight years.");
    }

    public IReadOnlyList<DateTimeOffset> Preview(DateTimeOffset after, TimeZoneInfo zone, int count = 5)
    {
        if (count is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(count));
        var result = new List<DateTimeOffset>();
        for (var i = 0; i < count; i++) { after = Next(after, zone); result.Add(after); }
        return result;
    }

    public string Describe()
    {
        static string Values(IEnumerable<int> values) => string.Join(", ", values.Order());
        var minute = fields[0].Count == 60 ? "Every minute" : "At minute(s) " + Values(fields[0]);
        var hour = fields[1].Count == 24 ? " of every hour" : " during hour(s) " + Values(fields[1]);
        var month = fields[3].Count == 12 ? "" : "; in month(s) " + Values(fields[3]);
        var weekdays = string.Join(", ", fields[4].Order().Select(d => CultureInfo.InvariantCulture.DateTimeFormat.DayNames[d]));
        var days = fields[2].Count == 31 && fields[4].Count == 7 ? "every day"
            : fields[2].Count == 31 ? weekdays
            : fields[4].Count == 7 ? "day(s) " + Values(fields[2]) + " of the month"
            : $"day(s) {Values(fields[2])} of the month {(anyDay || anyWeekday ? "and" : "or")} {weekdays}";
        return minute + hour + "; on " + days + month + ".";
    }

    private static HashSet<int> Parse(string text, int min, int max)
    {
        var result = new HashSet<int>();
        foreach (var item in text.Split(','))
        {
            var stepParts = item.Split('/');
            if (stepParts.Length > 2) throw new ArgumentException("Invalid cron step.");
            var step = stepParts.Length == 2 ? Number(stepParts[1], 1, max - min + 1) : 1;
            var range = stepParts[0].Split('-');
            int first, last;
            if (stepParts[0] == "*") { first = min; last = max; }
            else if (range.Length == 1) { first = Number(range[0], min, max); last = stepParts.Length == 2 ? max : first; }
            else if (range.Length == 2) { first = Number(range[0], min, max); last = Number(range[1], min, max); }
            else throw new ArgumentException("Invalid cron range.");
            if (first > last) throw new ArgumentException("Cron ranges cannot wrap.");
            for (var i = first; i <= last; i += step) result.Add(i);
        }
        return result;
    }

    private static int Number(string value, int min, int max) =>
        value.Length > 0 && value.All(char.IsAsciiDigit) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            && n >= min && n <= max ? n : throw new ArgumentException("Cron value is out of range.");
}
