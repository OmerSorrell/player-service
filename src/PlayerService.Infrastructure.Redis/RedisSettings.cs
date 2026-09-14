using StackExchange.Redis;

namespace PlayerService.Infrastructure.Redis;

public sealed class RedisSettings
{
    public string ConnectionString { get; set; } = "localhost:6379,abortConnect=false,asyncTimeout=2000,connectTimeout=2000";
    public string KeyPrefix { get; set; } = "player-service:";
    public int SessionSeconds { get; set; } = 1800;
    public int IdempotencySeconds { get; set; } = 86400;

    public ConfigurationOptions ToConfiguration()
    {
        var configuration = ConfigurationOptions.Parse(ConnectionString);
        configuration.BacklogPolicy = BacklogPolicy.FailFast;
        return configuration;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString) || string.IsNullOrWhiteSpace(KeyPrefix) || KeyPrefix.Length > 100 ||
            SessionSeconds is < 1 or > 86400 || IdempotencySeconds is < 1 or > 604800)
            throw new ArgumentException("Invalid Redis configuration: use a nonempty prefix, session 1–86400s, retention 1–604800s.");
    }
}
