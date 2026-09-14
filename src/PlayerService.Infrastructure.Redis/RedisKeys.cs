using StackExchange.Redis;

namespace PlayerService.Infrastructure.Redis;

internal sealed class RedisKeys(string prefix)
{
    public RedisKey Session(string token) => prefix + "session:" + token;
    public RedisKey Device(string deviceId) => prefix + "device:" + deviceId;
    public RedisKey Player(string playerId) => prefix + "player:" + playerId;
    public RedisKey PlayerSessions(string playerId) => prefix + "session:player:" + playerId;
    public RedisKey Leaderboard => prefix + "leaderboard:score";
    public RedisKey ScoreRequest(string playerId, Guid requestId) => prefix + $"score-request:{playerId}:{requestId:D}";
    public RedisKey GiftRequest(string playerId, Guid requestId) => prefix + $"gift-request:{playerId}:{requestId:D}";
}
