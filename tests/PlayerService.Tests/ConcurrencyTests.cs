using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PlayerService.Application;
using PlayerService.Application.Models;
using PlayerService.Infrastructure.Redis;
using StackExchange.Redis;
using Xunit;

namespace PlayerService.Tests;

// Each test has an isolated key prefix. Two independent API hosts/multiplexers share real Redis.
public sealed class ConcurrencyTests : IAsyncLifetime
{
    private readonly string prefix = $"player-tests:{Guid.NewGuid():N}:";
    private readonly string connectionString = Environment.GetEnvironmentVariable("TEST_REDIS") ?? "localhost:6379,abortConnect=false";
    private ApiFactory first = null!;
    private ApiFactory second = null!;
    private HttpClient a = null!;
    private HttpClient b = null!;
    private ConnectionMultiplexer redis = null!;

    public async Task InitializeAsync()
    {
        redis = await ConnectionMultiplexer.ConnectAsync(connectionString);
        await redis.GetDatabase().PingAsync(); // An unavailable dependency is a failure, never a skipped test.
        first = new(prefix, connectionString);
        second = new(prefix, connectionString);
        a = first.CreateClient();
        b = second.CreateClient();
    }

    public async Task DisposeAsync()
    {
        a?.Dispose(); b?.Dispose();
        if (first is not null) await first.DisposeAsync();
        if (second is not null) await second.DisposeAsync();
        if (redis is not null)
        {
            var server = redis.GetServer(redis.GetEndPoints()[0]);
            await foreach (var key in server.KeysAsync(pattern: prefix + "*"))
                await redis.GetDatabase().KeyDeleteAsync(key);
            await redis.DisposeAsync();
        }
    }

    [Fact]
    public async Task SimultaneousLoginHasOneWinnerAndSecondDeviceDoesNotResetScore()
    {
        var responses = await Race(20, i => (i % 2 == 0 ? a : b).PostAsJsonAsync("/login", new { playerId = "p", deviceId = "d" }));
        Assert.Single(responses, r => r.IsSuccessStatusCode);
        Assert.Equal(19, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var session = await responses.Single(r => r.IsSuccessStatusCode).Content.ReadFromJsonAsync<Session>();
        await Score(a, "p", session!.SessionToken, 20);
        var secondToken = await Login(b, "p", "d2");
        Assert.Equal(1020, (await Stats(b, "p", secondToken)).Score);
    }

    [Fact]
    public async Task ConcurrentDistinctScoreUpdatesAreNotLostAcrossInstances()
    {
        var token = await Login(a, "p");
        var responses = await Race(300, i => Score(i % 2 == 0 ? a : b, "p", token, 2));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1600, (await Stats(a, "p", token)).Score);
    }

    [Fact]
    public async Task ConcurrentDuplicatesReturnIdenticalSnapshotEvenAfterAnotherUpdate()
    {
        var token = await Login(a, "p");
        var id = Guid.NewGuid();
        var responses = await Race(100, i => Score(i % 2 == 0 ? a : b, "p", token, 5, id));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        Assert.Single(bodies.Distinct());
        await Score(a, "p", token, 10);
        Assert.Equal(bodies[0], await (await Score(b, "p", token, 5, id)).Content.ReadAsStringAsync());
        Assert.Equal(1015, (await Stats(a, "p", token)).Score);
        Assert.Equal(HttpStatusCode.Conflict, (await Score(b, "p", token, 6, id)).StatusCode);
    }

    [Fact]
    public async Task DuplicateGiftsTransferExactlyOnceAndReturnOriginalReceipt()
    {
        var sender = await Login(a, "sender");
        var recipient = await Login(b, "recipient");
        var id = Guid.NewGuid();
        var responses = await Race(100, i => Gift(i % 2 == 0 ? a : b, "sender", sender, "recipient", 50, id));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        Assert.Single(bodies.Distinct());
        Assert.Equal(950, (await Stats(a, "sender", sender)).Score);
        Assert.Equal(1050, (await Stats(b, "recipient", recipient)).Score);
        Assert.Equal(1, (await Stats(a, "sender", sender)).GiftsSent);
        Assert.Equal(1, (await Stats(b, "recipient", recipient)).GiftsReceived);
        Assert.Equal(HttpStatusCode.Conflict, (await Gift(a, "sender", sender, "recipient", 51, id)).StatusCode);
    }

    [Fact]
    public async Task OverlappingBidirectionalGiftsConservePointsThroughoutExecution()
    {
        const int players = 12, transfers = 1200;
        var random = new Random(20260913);
        // Start with every directed pair, then add a reproducible mixed workload. Each sender
        // sends exactly 100 points, so scheduling cannot cause insufficient-balance rejections.
        var pairs = Enumerable.Range(0, transfers).Select(i =>
        {
            var from = i % players;
            var offset = i < players * (players - 1) ? i / players + 1 : random.Next(1, players);
            return (From: from, To: (from + offset) % players);
        }).ToArray();
        Assert.Equal(players * (players - 1), pairs.Distinct().Count());
        Assert.All(Enumerable.Range(0, players), player =>
        {
            Assert.Equal(players - 1, pairs.Where(pair => pair.From == player).Select(pair => pair.To).Distinct().Count());
            Assert.Contains((player, (player + 1) % players), pairs); // Includes a cycle through all players.
        });
        random.Shuffle(pairs);
        var tokens = new string[players];
        for (var i = 0; i < players; i++) tokens[i] = await Login(a, $"p{i}");
        var pending = Race(transfers, i =>
        {
            var (from, to) = pairs[i];
            return Gift(i % 2 == 0 ? a : b, $"p{from}", tokens[from], $"p{to}", 1);
        });
        do
        {
            var board = await Board(b, tokens[0]);
            Assert.Equal(players * 1000, board.Sum(p => p.Score));
            Assert.All(board, p => Assert.True(p.Score >= 0));
        } while (!pending.IsCompleted);
        var responses = await pending;
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var final = await Task.WhenAll(Enumerable.Range(0, players).Select(i => Stats(a, $"p{i}", tokens[i])));
        Assert.Equal(players * 1000, final.Sum(p => p.Score));
        Assert.Equal(transfers, final.Sum(p => p.GiftsSent));
        Assert.Equal(transfers, final.Sum(p => p.GiftsReceived));
        for (var i = 0; i < players; i++)
        {
            var sent = pairs.Count(pair => pair.From == i);
            var received = pairs.Count(pair => pair.To == i);
            Assert.Equal(sent, final[i].GiftsSent);
            Assert.Equal(received, final[i].GiftsReceived);
            Assert.Equal(1000 + received - sent, final[i].Score);
        }
        var leaderboard = await Board(a, tokens[0]);
        Assert.Equal(leaderboard.OrderByDescending(p => p.Score).Select(p => p.Score), leaderboard.Select(p => p.Score));
        Assert.All(final, p => Assert.Equal(p.Score, leaderboard.Single(row => row.PlayerId == p.PlayerId).Score));
    }

    [Fact]
    public async Task ConcurrentOverspendingNeverMakesBalanceNegative()
    {
        var token = await Login(a, "s");
        var recipient = await Login(b, "r");
        var responses = await Race(100, i => Gift(i % 2 == 0 ? a : b, "s", token, "r", 100));
        Assert.Equal(10, responses.Count(r => r.IsSuccessStatusCode));
        Assert.Equal(90, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(0, (await Stats(a, "s", token)).Score);
        Assert.Equal(2000, (await Stats(b, "r", recipient)).Score);
    }

    [Fact]
    public async Task ScoreAddsAndGiftsAreSerializedTogether()
    {
        var token = await Login(a, "s");
        var recipient = await Login(b, "r");
        var responses = await Race(400, i => i % 2 == 0 ? Score(a, "s", token, 1) : Gift(b, "s", token, "r", 1));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1000, (await Stats(a, "s", token)).Score);
        Assert.Equal(1200, (await Stats(b, "r", recipient)).Score);
    }

    [Fact]
    public async Task OfflineAndInsufficientBalanceRejectionsAreIdempotent()
    {
        var token = await Login(a, "s");
        var offlineId = Guid.NewGuid();
        var offline = await Gift(a, "s", token, "r", 1, offlineId);
        Assert.Equal(HttpStatusCode.Conflict, offline.StatusCode);
        await Login(b, "r");
        Assert.Equal(await offline.Content.ReadAsStringAsync(), await (await Gift(b, "s", token, "r", 1, offlineId)).Content.ReadAsStringAsync());
        var balanceId = Guid.NewGuid();
        var insufficient = await Gift(a, "s", token, "r", 1100, balanceId);
        Assert.Equal(HttpStatusCode.Conflict, insufficient.StatusCode);
        await Score(a, "s", token, 1000);
        Assert.Equal(await insufficient.Content.ReadAsStringAsync(), await (await Gift(b, "s", token, "r", 1100, balanceId)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ExpiredSessionRejectsRequestsAndReleasesDevice()
    {
        var store = new RedisPlayerStore(redis, new RedisSettings { ConnectionString = connectionString, KeyPrefix = prefix, SessionSeconds = 1 });
        var login = await store.LoginAsync("p", "d");
        Assert.True(login.IsSuccess);
        await Task.Delay(1150);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(a, HttpMethod.Get, "/players/p/stats", login.Value!.SessionToken)).StatusCode);
        var sender = await Login(a, "s");
        Assert.Equal(HttpStatusCode.Conflict, (await Gift(a, "s", sender, "p", 1)).StatusCode);
        Assert.NotEqual(login.Value.SessionToken, await Login(a, "p", "d"));
    }

    [Fact]
    public async Task SuccessfulActivityRenewsSlidingExpiry()
    {
        var store = new RedisPlayerStore(redis, new RedisSettings { ConnectionString = connectionString, KeyPrefix = prefix });
        var token = (await store.LoginAsync("p", "d")).Value!.SessionToken;
        // Simulate an almost-idle lease without a timing-sensitive sleep on a developer laptop.
        var database = redis.GetDatabase();
        await database.KeyExpireAsync(prefix + "session:" + token, TimeSpan.FromMinutes(1));
        await database.KeyExpireAsync(prefix + "device:d", TimeSpan.FromMinutes(1));
        await database.SortedSetAddAsync(prefix + "session:player:p", token, DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds());
        Assert.True((await store.GetAsync(token, "p")).IsSuccess);
        Assert.True(await database.KeyTimeToLiveAsync(prefix + "session:" + token) > TimeSpan.FromMinutes(29));
        Assert.True(await database.KeyTimeToLiveAsync(prefix + "device:d") > TimeSpan.FromMinutes(29));
        Assert.True(await database.SortedSetScoreAsync(prefix + "session:player:p", token) > DateTimeOffset.UtcNow.AddMinutes(29).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task SupersededTokenAndForeignMutationsAreRejected()
    {
        var token = await Login(a, "p", "d");
        await Login(a, "other");
        Assert.Equal(HttpStatusCode.Forbidden, (await Score(a, "other", token, 1)).StatusCode);
        await redis.GetDatabase().StringSetAsync(prefix + "device:d", Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(30));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Score(a, "p", token, 1)).StatusCode);
    }

    [Fact]
    public async Task InvalidInputsAndOverflowDoNotChangeBalances()
    {
        var token = await Login(a, "s");
        var recipient = await Login(a, "r");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Score(a, "s", token, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Score(a, "s", token, -1)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Score(a, "s", token, 1, Guid.Empty)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Gift(a, "s", token, "s", 1)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(a, HttpMethod.Get, "/leaderboard?n=101", token)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await a.PostAsJsonAsync("/login", new { playerId = "bad:id", deviceId = "d" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.GetAsync("/leaderboard")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Score(a, "r", recipient, PlayerOperations.MaximumScore - 1000)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Gift(a, "s", token, "r", 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Score(a, "r", recipient, 1)).StatusCode);
        Assert.Equal(1000, (await Stats(a, "s", token)).Score);
        Assert.Equal(PlayerOperations.MaximumScore, (await Stats(a, "r", recipient)).Score);
    }

    [Fact]
    public async Task WrongKeyTypeFailsBeforeDebit()
    {
        var token = await Login(a, "s");
        await Login(a, "r");
        var board = prefix + "leaderboard:score";
        await redis.GetDatabase().KeyDeleteAsync(board);
        await redis.GetDatabase().StringSetAsync(board, "wrong type");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Gift(a, "s", token, "r", 10)).StatusCode);
        Assert.Equal(1000, (await Stats(a, "s", token)).Score);
        Assert.Equal(1000, (await Stats(a, "r", token)).Score);
    }

    [Fact]
    public async Task LostGiftResponseCanBeRetriedThroughAnotherInstance()
    {
        var sender = await Login(a, "s");
        var recipient = await Login(b, "r");
        var id = Guid.NewGuid();
        // Commit the operation, then discard its response as an unreliable mobile client might.
        (await Gift(a, "s", sender, "r", 17, id)).Dispose();
        using var retried = await Gift(b, "s", sender, "r", 17, id);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(983, (await Stats(a, "s", sender)).Score);
        Assert.Equal(1017, (await Stats(b, "r", recipient)).Score);
        Assert.Equal(1, (await Stats(a, "s", sender)).GiftsSent);
    }

    [Fact]
    public async Task UnavailableRedisReturnsRetryable503()
    {
        await using var unavailable = new ApiFactory(prefix, "localhost:6398,abortConnect=false,connectTimeout=100,asyncTimeout=100,connectRetry=0");
        using var client = unavailable.CreateClient();
        using var response = await client.PostAsJsonAsync("/login", new { playerId = "s", deviceId = "s" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("storage_unavailable", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task MetricsExposeBoundedOperationLabels()
    {
        var token = await Login(a, "private_player");
        await Score(a, "private_player", token, 1);
        var metrics = await a.GetStringAsync("/metrics");
        Assert.Contains("player_operations", metrics);
        Assert.Contains("player_redis_duration", metrics);
        Assert.Contains("operation=\"Login\"", metrics);
        Assert.Contains("operation=\"Score\"", metrics);
        Assert.DoesNotContain(token, metrics);
        Assert.DoesNotContain("private_player", metrics);
    }

    private static async Task<T[]> Race<T>(int count, Func<int, Task<T>> action)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count).Select(async i => { await gate.Task; return await action(i); }).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    private static async Task<string> Login(HttpClient client, string player, string? device = null)
    {
        using var response = await client.PostAsJsonAsync("/login", new { playerId = player, deviceId = device ?? player });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Session>())!.SessionToken;
    }

    private static Task<HttpResponseMessage> Score(HttpClient client, string player, string token, long points, Guid? id = null) =>
        Send(client, HttpMethod.Post, $"/players/{player}/stats/score", token, new { points, requestId = id ?? Guid.NewGuid() });
    private static Task<HttpResponseMessage> Gift(HttpClient client, string player, string token, string recipient, long points, Guid? id = null) =>
        Send(client, HttpMethod.Post, $"/players/{player}/gifts", token, new { toPlayerId = recipient, points, requestId = id ?? Guid.NewGuid() });

    private static async Task<PlayerStats> Stats(HttpClient client, string player, string token)
    {
        using var response = await Send(client, HttpMethod.Get, $"/players/{player}/stats", token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PlayerStats>())!;
    }

    private static async Task<LeaderboardEntry[]> Board(HttpClient client, string token)
    {
        using var response = await Send(client, HttpMethod.Get, "/leaderboard?n=100", token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LeaderboardEntry[]>())!;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private sealed class ApiFactory(string prefix, string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureServices(services =>
        {
            services.RemoveAll<RedisSettings>();
            services.RemoveAll<IConnectionMultiplexer>();
            services.AddSingleton(new RedisSettings { ConnectionString = connectionString, KeyPrefix = prefix });
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(new RedisSettings { ConnectionString = connectionString }.ToConfiguration()));
        });
    }
}
