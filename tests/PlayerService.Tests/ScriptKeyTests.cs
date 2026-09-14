using System.Reflection;
using PlayerService.Infrastructure.Redis;
using StackExchange.Redis;
using Xunit;

namespace PlayerService.Tests;

public sealed class ScriptKeyTests : IAsyncLifetime
{
    private readonly string prefix = $"player-key-tests:{Guid.NewGuid():N}:";
    private ConnectionMultiplexer connection = null!;
    private IDatabase database = null!;
    private RedisPlayerStore store = null!;
    private ScriptDatabase interceptor = null!;

    public async Task InitializeAsync()
    {
        connection = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("TEST_REDIS") ?? "localhost:6379,abortConnect=false");
        database = connection.GetDatabase();
        await database.PingAsync();
        var proxy = DispatchProxy.Create<IDatabase, ScriptDatabase>();
        interceptor = (ScriptDatabase)proxy;
        interceptor.Database = database;
        var connectionProxy = DispatchProxy.Create<IConnectionMultiplexer, ScriptConnection>();
        ((ScriptConnection)connectionProxy).Database = proxy;
        store = new RedisPlayerStore(connectionProxy, new RedisSettings { KeyPrefix = prefix });
    }

    public async Task DisposeAsync()
    {
        if (connection is null) return;
        await foreach (var key in connection.GetServer(connection.GetEndPoints()[0]).KeysAsync(pattern: prefix + "*"))
            await database.KeyDeleteAsync(key);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task EveryScriptPathUsesDeclaredKeys()
    {
        await store.InitializeAsync();
        var first = (await store.LoginAsync("p1", "d1")).Value!.SessionToken;
        var second = (await store.LoginAsync("p2", "d2")).Value!.SessionToken;
        Assert.Equal("device_active", (await store.LoginAsync("p2", "d1")).Error);
        Assert.True((await store.GetAsync(first, "p2")).IsSuccess); // Actor differs from read target.
        Assert.True((await store.GetTopAsync(first, 10)).IsSuccess);
        var scoreId = Guid.NewGuid();
        Assert.True((await store.AddAsync(first, "p1", 10, scoreId)).IsSuccess);
        Assert.True((await store.AddAsync(first, "p1", 10, scoreId)).IsReplay);
        var giftId = Guid.NewGuid();
        Assert.True((await store.ExecuteAsync(first, "p1", "p2", 5, giftId)).IsSuccess);
        Assert.True((await store.ExecuteAsync(first, "p1", "p2", 5, giftId)).IsReplay);
        Assert.Equal("idempotency_conflict", (await store.ExecuteAsync(first, "p1", "p2", 6, giftId)).Error);
        var offlineId = Guid.NewGuid();
        Assert.Equal("recipient_offline", (await store.ExecuteAsync(first, "p1", "missing", 5, offlineId)).Error);
        Assert.True((await store.ExecuteAsync(first, "p1", "missing", 5, offlineId)).IsReplay);
        Assert.Equal("forbidden", (await store.AddAsync(second, "p1", 1, Guid.NewGuid())).Error);
        Assert.Equal("unauthorized", (await store.GetAsync(Guid.NewGuid().ToString("N"), "p1")).Error);
        // Exercise cleanup: removing expired sorted-set members must not access their session keys.
        await database.SortedSetAddAsync(prefix + "session:player:p1", "old-token", 0);
        Assert.True((await store.GetAsync(first, "p1")).IsSuccess);
        Assert.Null(await database.SortedSetScoreAsync(prefix + "session:player:p1", "old-token"));
        // A dangling device index is repaired using its explicitly declared prior-session key.
        await database.StringSetAsync(prefix + "device:d3", "missing-token", TimeSpan.FromMinutes(1));
        Assert.True((await store.LoginAsync("p3", "d3")).IsSuccess);
    }

    [Fact]
    public async Task AnotherDeviceKeepsRecipientOnlineAfterFirstDeviceExpires()
    {
        var shortLeaseStore = new RedisPlayerStore(connection, new RedisSettings { KeyPrefix = prefix, SessionSeconds = 1 });
        var expiredToken = (await shortLeaseStore.LoginAsync("recipient", "phone")).Value!.SessionToken;
        var activeToken = (await store.LoginAsync("recipient", "tablet")).Value!.SessionToken;
        var sender = (await store.LoginAsync("sender", "sender-device")).Value!.SessionToken;
        await Task.Delay(1150);
        Assert.Equal("unauthorized", (await store.GetAsync(expiredToken, "recipient")).Error);
        Assert.True((await store.ExecuteAsync(sender, "sender", "recipient", 5, Guid.NewGuid())).IsSuccess);
        Assert.Equal(1005, (await store.GetAsync(activeToken, "recipient")).Value!.Score);
    }

    [Fact]
    public async Task SessionRemovedAfterResolutionCannotExecuteOrBeRenewed()
    {
        var token = (await store.LoginAsync("p1", "d1")).Value!.SessionToken;
        interceptor.BeforeScript = async () => { await database.KeyDeleteAsync(prefix + "session:" + token); };
        var requestId = Guid.NewGuid();
        Assert.Equal("unauthorized", (await store.AddAsync(token, "p1", 5, requestId)).Error);
        Assert.Equal("1000", (string?)await database.HashGetAsync(prefix + "player:p1", "score"));
        Assert.False(await database.KeyExistsAsync(prefix + $"score-request:p1:{requestId:D}"));
        Assert.False(await database.KeyExistsAsync(prefix + "session:" + token));
    }

    [Fact]
    public async Task DeviceReplacedAfterResolutionCannotTransferOrOverwriteTheNewToken()
    {
        var token = (await store.LoginAsync("p1", "d1")).Value!.SessionToken;
        await store.LoginAsync("p2", "d2");
        var replacement = Guid.NewGuid().ToString("N");
        interceptor.BeforeScript = async () => { await database.StringSetAsync(prefix + "device:d1", replacement, TimeSpan.FromMinutes(30)); };
        var requestId = Guid.NewGuid();
        Assert.Equal("unauthorized", (await store.ExecuteAsync(token, "p1", "p2", 5, requestId)).Error);
        Assert.Equal("1000", (string?)await database.HashGetAsync(prefix + "player:p1", "score"));
        Assert.Equal("1000", (string?)await database.HashGetAsync(prefix + "player:p2", "score"));
        Assert.Equal(replacement, (string?)await database.StringGetAsync(prefix + "device:d1"));
        Assert.False(await database.KeyExistsAsync(prefix + $"gift-request:p1:{requestId:D}"));
    }

    [Fact]
    public async Task SessionIdentityChangedAfterResolutionCannotUseStalePlayerKeys()
    {
        var token = (await store.LoginAsync("p1", "d1")).Value!.SessionToken;
        await store.LoginAsync("p2", "d2");
        interceptor.BeforeScript = async () =>
        {
            await database.HashSetAsync(prefix + "session:" + token, [new("playerId", "p2"), new("deviceId", "d2")]);
        };
        Assert.Equal("unauthorized", (await store.GetAsync(token, "p2")).Error);
        Assert.Equal("1000", (string?)await database.HashGetAsync(prefix + "player:p1", "score"));
        Assert.Equal("1000", (string?)await database.HashGetAsync(prefix + "player:p2", "score"));
    }

    [Fact]
    public async Task CompetingLoginAfterResolutionCannotBeOverwritten()
    {
        var competing = new RedisPlayerStore(connection, new RedisSettings { KeyPrefix = prefix });
        string? winningToken = null;
        interceptor.BeforeScript = async () => { winningToken = (await competing.LoginAsync("winner", "device")).Value!.SessionToken; };
        Assert.Equal("device_active", (await store.LoginAsync("loser", "device")).Error);
        Assert.NotNull(winningToken);
        Assert.Equal(winningToken, (string?)await database.StringGetAsync(prefix + "device:device"));
        Assert.False(await database.KeyExistsAsync(prefix + "player:loser"));
        Assert.True((await competing.GetAsync(winningToken!, "winner")).IsSuccess);
    }

    // These proxies wrap real Redis. The callback runs after C# resolves keys, immediately
    // before the actual Lua evaluation, so race tests need no timing-dependent sleeps.
    public class ScriptConnection : DispatchProxy
    {
        public IDatabase Database { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == nameof(IConnectionMultiplexer.GetDatabase) ? Database : throw new NotSupportedException();
    }

    public class ScriptDatabase : DispatchProxy
    {
        public IDatabase Database { get; set; } = null!;
        public Func<Task>? BeforeScript { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IDatabase.ScriptEvaluateAsync) && args![0] is string script)
                return EvaluateAsync(script, (RedisKey[])args[1]!, (RedisValue[])args[2]!, (CommandFlags)args[3]!);
            return targetMethod.Invoke(Database, args);
        }

        private async Task<RedisResult> EvaluateAsync(string script, RedisKey[] keys, RedisValue[] values, CommandFlags flags)
        {
            var callback = BeforeScript;
            BeforeScript = null;
            if (callback is not null) await callback();
            // All commands currently used by these scripts have one key in their first
            // argument, except TIME. Shadow redis.call locally to enforce that contract.
            const string guard = """
                local declared = {}
                for _, key in ipairs(KEYS) do declared[key] = true end
                local execute = redis.call
                local redis = {call = function(command, ...)
                    local args = {...}
                    if command ~= 'TIME' and not declared[args[1]] then
                        error('Script accessed an undeclared Redis key')
                    end
                    return execute(command, unpack(args))
                end}

                """;
            return await Database.ScriptEvaluateAsync(guard + script, keys, values, flags);
        }
    }
}
