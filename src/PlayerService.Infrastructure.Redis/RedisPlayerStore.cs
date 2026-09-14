using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlayerService.Application.Abstractions;
using PlayerService.Application.Models;
using StackExchange.Redis;

namespace PlayerService.Infrastructure.Redis;

// Every method maps to a single script; no application code can split a transfer.
public sealed class RedisPlayerStore : ISessionStore, IScoreUpdateStore, IGiftStore, IPlayerStatsStore, ILeaderboardStore
{
    private readonly IDatabase database;
    private readonly RedisSettings settings;
    private readonly RedisKeys keys;
    private readonly IReadOnlyDictionary<RedisOperation, string> scripts;
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    public RedisPlayerStore(IConnectionMultiplexer connection, RedisSettings settings)
    {
        settings.Validate();
        this.settings = settings;
        keys = new RedisKeys(settings.KeyPrefix);
        database = connection.GetDatabase();
        var common = ReadScript("Common");
        scripts = Enum.GetValues<RedisOperation>()
            .ToDictionary(operation => operation, operation => common + "\n" + ReadScript(OperationName(operation)));
    }

    public Task<Outcome<Session>> LoginAsync(string playerId, string deviceId) =>
        RunAsync<Session>(RedisOperation.Login, Guid.NewGuid().ToString("N"), playerId, deviceId);

    public async Task InitializeAsync()
    {
        // Compile and populate the client's SHA cache without reading or writing application keys.
        // The explicit warmup flag exits before any key access in Common.lua.
        foreach (var script in scripts.Values)
            await database.ScriptEvaluateAsync(script, [],
                ["", "", "", 0, "", settings.SessionSeconds, settings.IdempotencySeconds, "", "", "", "warmup"],
                CommandFlags.DemandMaster);
    }
    public Task<Outcome<PlayerStats>> GetAsync(string token, string playerId) => RunAsync<PlayerStats>(RedisOperation.Stats, token, playerId);
    public Task<Outcome<PlayerStats>> AddAsync(string token, string playerId, long points, Guid requestId) =>
        RunAsync<PlayerStats>(RedisOperation.Score, token, playerId, "", points, requestId);
    public Task<Outcome<GiftReceipt>> ExecuteAsync(string token, string playerId, string toPlayerId, long points, Guid requestId) =>
        RunAsync<GiftReceipt>(RedisOperation.Gift, token, playerId, toPlayerId, points, requestId);
    public async Task<Outcome<LeaderboardEntry[]>> GetTopAsync(string token, int count)
    {
        var outcome = await RunAsync<LeaderboardEntry[]>(RedisOperation.Leaderboard, token, "", "", count);
        if (outcome.IsSuccess) ServiceMetrics.LeaderboardSize.Record(outcome.Value!.Length);
        return outcome;
    }

    private async Task<Outcome<T>> RunAsync<T>(RedisOperation operation, string token, string playerId, string otherId = "", long points = 0, Guid requestId = default)
    {
        var operationName = OperationName(operation);
        var started = Stopwatch.GetTimestamp();
        try
        {
            // Resolve only the identities needed to declare keys. The script rereads and validates
            // the session/device mapping before any mutation; this lookup does not authenticate.
            var actorId = playerId;
            var deviceId = otherId;
            var previousToken = "";
            if (operation == RedisOperation.Login)
            {
                previousToken = (string?)await database.StringGetAsync(keys.Device(deviceId), CommandFlags.DemandMaster) ?? "";
            }
            else
            {
                var identity = await database.HashGetAsync(keys.Session(token), ["playerId", "deviceId"], CommandFlags.DemandMaster);
                actorId = (string?)identity[0] ?? "";
                deviceId = (string?)identity[1] ?? "";
            }

            // KEYS[1..5] are the common session/device/actor/index/leaderboard keys.
            // Subsequent keys belong to the selected operation; no Lua code derives key names.
            RedisKey[] commonKeys = [keys.Session(token), keys.Device(deviceId), keys.Player(actorId), keys.PlayerSessions(actorId), keys.Leaderboard];
            RedisValue[] arguments = [token, playerId, otherId, points, requestId.ToString("D"), settings.SessionSeconds, settings.IdempotencySeconds, actorId, deviceId, previousToken, ""];
            RedisKey[] scriptKeys = operation switch
            {
                RedisOperation.Login => [.. commonKeys, keys.Session(previousToken)],
                RedisOperation.Stats => [.. commonKeys, keys.Player(playerId)],
                RedisOperation.Score => [.. commonKeys, keys.ScoreRequest(playerId, requestId)],
                RedisOperation.Gift => [.. commonKeys, keys.Player(otherId), keys.PlayerSessions(otherId), keys.GiftRequest(playerId, requestId)],
                RedisOperation.Leaderboard => commonKeys,
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            // Explicit keys do not remove Redis Cluster's cross-slot restriction for gifts.
            var raw = await database.ScriptEvaluateAsync(scripts[operation], scriptKeys,
                arguments,
                CommandFlags.DemandMaster);
            var parts = (RedisResult[])raw!;
            var code = (string)parts[0]!;
            var replay = (long)parts[2] == 1;
            ServiceMetrics.Outcomes.Add(1, new("operation", operationName), new("outcome", code), new("replay", replay));
            return code == "ok"
                ? new(JsonSerializer.Deserialize<T>((string)parts[1]!, Json)!, IsReplay: replay)
                : new(default, code, replay);
        }
        catch (Exception exception) when (exception is RedisException or RedisTimeoutException)
        {
            ServiceMetrics.RedisFailures.Add(1, new("operation", operationName), new("kind", exception is RedisTimeoutException ? "timeout" : "error"));
            throw;
        }
        finally
        {
            ServiceMetrics.RedisDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, new KeyValuePair<string, object?>("operation", operationName));
        }
    }

    // Resource names and metric labels are external contracts; keep them stable if enum members are renamed.
    private static string OperationName(RedisOperation operation) => operation switch
    {
        RedisOperation.Login => "Login",
        RedisOperation.Stats => "Stats",
        RedisOperation.Score => "Score",
        RedisOperation.Gift => "Gift",
        RedisOperation.Leaderboard => "Leaderboard",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown Redis operation.")
    };

    private static string ReadScript(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"PlayerService.Infrastructure.Redis.Scripts.{name}.lua")
            ?? throw new InvalidOperationException($"Missing script {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new UnixMillisecondsConverter());
        return options;
    }

    private sealed class UnixMillisecondsConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64());
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value.ToUnixTimeMilliseconds());
    }
}
