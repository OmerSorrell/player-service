using System.Diagnostics.Metrics;

namespace PlayerService.Infrastructure.Redis;

public static class ServiceMetrics
{
    public const string MeterName = "PlayerService";
    public static readonly Meter Meter = new(MeterName);
    public static readonly Histogram<double> RedisDuration = Meter.CreateHistogram<double>("player.redis.duration", "s");
    public static readonly Counter<long> RedisFailures = Meter.CreateCounter<long>("player.redis.failures");
    public static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("player.operations");
    public static readonly Histogram<long> LeaderboardSize = Meter.CreateHistogram<long>("player.leaderboard.entries");
}
