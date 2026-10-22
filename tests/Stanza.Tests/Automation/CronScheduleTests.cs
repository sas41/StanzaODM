using Stanza.Core.Automation;

namespace Stanza.Tests.Automation;

public sealed class CronScheduleTests
{
    [Theory]
    [InlineData("*/15 * * * *", "2026-10-22T12:07:45Z", "2026-10-22T12:15:00Z")]
    [InlineData("  */15\t*  * * *  ", "2026-10-22T12:07:45Z", "2026-10-22T12:15:00Z")]
    [InlineData("1,31 8-18/2 * * 1-5", "2026-10-22T12:01:00Z", "2026-10-22T12:31:00Z")]
    [InlineData("0 0 29 2 *", "2025-01-01T00:00:00Z", "2028-02-29T00:00:00Z")]
    [InlineData("0 0 1 * 1", "2026-10-22T12:00:00Z", "2026-10-26T00:00:00Z")]
    [InlineData("0 0 * * 7", "2026-10-22T12:00:00Z", "2026-10-25T00:00:00Z")]
    [InlineData("5/20 * * * *", "2026-10-22T12:26:00Z", "2026-10-22T12:45:00Z")]
    [InlineData("0 0 */1 * 1", "2026-10-22T12:00:00Z", "2026-10-26T00:00:00Z")]
    [InlineData("0 0 */2 * 1", "2026-10-22T12:00:00Z", "2026-11-09T00:00:00Z")]
    public void RecurrencesAreExclusiveAndSupportRangesListsStepsAndSunday(string expression, string after, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), new CronSchedule(expression).Next(DateTimeOffset.Parse(after), TimeZoneInfo.Utc));

    [Theory]
    [InlineData("* * * *")][InlineData("* * * * * *")][InlineData("60 * * * *")][InlineData("*/0 * * * *")]
    [InlineData("* 24 * * *")][InlineData("* * 0 * *")][InlineData("* * * 13 *")][InlineData("* * * * 8")]
    [InlineData("1-0 * * * *")][InlineData("* * * * MON")][InlineData("-1 * * * *")][InlineData("1,,2 * * * *")]
    [InlineData("* * * * 1/2/3")]
    public void InvalidExpressionsAreRejected(string expression) => Assert.Throws<ArgumentException>(() => new CronSchedule(expression));

    [Fact]
    public void ImpossibleCalendarDateIsBounded() => Assert.Throws<ArgumentException>(() =>
        new CronSchedule("0 0 31 2 *").Next(DateTimeOffset.Parse("2026-01-01Z"), TimeZoneInfo.Utc));

    [Fact]
    public void SpringGapIsSkippedRatherThanShifted()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        Assert.Equal(DateTimeOffset.Parse("2026-03-09T06:30:00Z"),
            new CronSchedule("30 2 * * *").Next(DateTimeOffset.Parse("2026-03-08T05:00:00Z"), zone));
    }

    [Fact]
    public void AutumnFoldRunsOnceAtEarlierUtcInstant()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var cron = new CronSchedule("30 1 * * *");
        var first = cron.Next(DateTimeOffset.Parse("2026-11-01T04:00:00Z"), zone);
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T05:30:00Z"), first);
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T06:30:00Z"), cron.Next(first, zone));
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T06:30:00Z"), cron.Next(DateTimeOffset.Parse("2026-11-01T06:00:00Z"), zone));
    }

    [Fact]
    public void PreviewHasBoundedCountAndConvertsNonWholeHourZones()
    {
        var cron = new CronSchedule("0 9 * * *");
        var runs = cron.Preview(DateTimeOffset.Parse("2026-10-22T00:00:00Z"), TimeZoneInfo.FindSystemTimeZoneById("Asia/Kathmandu"));
        Assert.Equal(5, runs.Count); Assert.Equal(DateTimeOffset.Parse("2026-10-22T03:15:00Z"), runs[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => cron.Preview(DateTimeOffset.UtcNow, TimeZoneInfo.Utc, 100));
    }

    [Fact]
    public void HumanReadableDescriptionExplainsSelectedTimesAndDayCombination()
    {
        var description = new CronSchedule("*/15 8-10 1 * 1").Describe();
        Assert.Contains("0, 15, 30, 45", description); Assert.Contains("8, 9, 10", description);
        Assert.Contains("day(s) 1 of the month or Monday", description);
        Assert.Contains("Every minute", new CronSchedule("* * * * *").Describe());
        Assert.Contains("and Monday", new CronSchedule("0 0 */2 * 1").Describe());
    }
}
