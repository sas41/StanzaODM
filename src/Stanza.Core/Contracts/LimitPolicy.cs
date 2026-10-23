namespace Stanza.Core.Contracts;

/// <summary>Zero consistently means no bound, no expiry, or no periodic refresh.</summary>
public static class LimitPolicy
{
    public static bool Exceeded(long value, long limit) => limit > 0 && value > limit;
    public static TimeSpan Duration(int seconds) => seconds == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);
}
