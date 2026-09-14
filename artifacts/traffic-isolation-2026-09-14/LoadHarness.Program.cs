using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

var urls = (args.ElementAtOrDefault(0) ?? "http://localhost:5080").Split(',');
var seconds = int.Parse(args.ElementAtOrDefault(1) ?? "20");
// Hot requests launched per 10ms tick; requests do not wait for prior responses.
var hotBatch = int.Parse(args.ElementAtOrDefault(2) ?? "32");
if (seconds is < 1 or > 120 || hotBatch is < 1 or > 256) throw new ArgumentException("Duration 1–120 seconds; hot batch 1–256.");
var clients = urls.Select(url => new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 1024 })
    { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) }).ToArray();
var run = "load_" + Guid.NewGuid().ToString("N")[..12];
var players = new List<(string Id, string Token)>();
for (var i = 0; i < 34; i++)
{
    var id = $"{run}_{i}";
    using var response = await SetupRequest(() => clients[i % clients.Length].PostAsJsonAsync("/login", new { playerId = id, deviceId = id }));
    response.EnsureSuccessStatusCode();
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    players.Add((id, json.RootElement.GetProperty("sessionToken").GetString()!));
}
// Resolve the same sessions on every API before measurement, including cold identities.
foreach (var player in players)
    foreach (var client in clients)
    {
        using var response = await SetupRequest(() => Send(client, player, false, 0));
        response.EnsureSuccessStatusCode();
    }
await Phase("warmup", hotBatch, 3);
await Task.Delay(2000);
var baseline = await Phase("cold_only", 0, seconds);
await Task.Delay(2000);
var mixed = await Phase("hot_gifts_and_cold_reads", hotBatch, seconds);
await Task.Delay(1000);
long total = 0;
foreach (var player in players)
{
    using var response = await SetupRequest(() => Send(clients[0], player, false, 0));
    response.EnsureSuccessStatusCode();
    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var score = body.RootElement.GetProperty("score").GetInt64();
    if (score < 0) throw new InvalidOperationException("Negative balance!");
    total += score;
}
if (total != players.Count * 1000) throw new InvalidOperationException($"Conservation failed: {total}");
var output = JsonSerializer.Serialize(new
{
    timestamp = DateTimeOffset.UtcNow, urls, seconds, hotBatch, targetHotRequestsPerSecond = hotBatch * 100,
    targetColdRequestsPerSecond = 80, run, baseline, mixed, conservedPoints = total,
    note = "Open-loop batches every 10ms; four cold reads every 50ms. Both hot players use every API. Latencies include body download and client connection waiting. Successful, rejected and all-response percentiles are separate. Generator skips late ticks or launches over 8192 in-flight and reports these explicitly. No retries/backoff during measurement. Localhost diagnostic, not a production isolation guarantee."
}, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(output);
if (args.Length > 3)
{
    var path = Path.GetFullPath(args[3]);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllTextAsync(path, output);
}
foreach (var client in clients) client.Dispose();

static async Task<HttpResponseMessage> SetupRequest(Func<Task<HttpResponseMessage>> send)
{
    for (var attempt = 0; ; attempt++)
    {
        var response = await send();
        if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt >= 30) return response;
        var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
        response.Dispose(); await Task.Delay(delay);
    }
}
async Task<object> Phase(string name, int batch, int duration)
{
    var cold = new Measurements();
    var hot = new Measurements();
    var inFlight = new ConcurrentDictionary<long, Task>();
    long sequence = 0;
    var missedTicks = 0;
    var skippedHot = 0;
    var skippedCold = 0;
    var start = Stopwatch.GetTimestamp();
    var ticks = duration * 100;
    for (var tick = 0; tick < ticks; tick++)
    {
        var delay = tick * 10 - Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay));
        if (Stopwatch.GetElapsedTime(start).TotalMilliseconds > tick * 10 + 10)
        { missedTicks++; skippedHot += batch; if (tick % 5 == 0) skippedCold += 4; continue; }
        for (var i = 0; i < (tick % 5 == 0 ? 4 : 0) + batch; i++)
        {
            var coldCount = tick % 5 == 0 ? 4 : 0;
            var isHot = i >= coldCount;
            var worker = isHot ? i - coldCount : i;
            if (inFlight.Count >= 8192) { if (isHot) skippedHot++; else skippedCold++; continue; }
            var id = ++sequence;
            var task = Measure(worker, isHot);
            inFlight[id] = task;
            _ = RemoveWhenDone(id, task);
        }
    }
    var offeredSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
    await Task.WhenAll(inFlight.Values);
    return new { name, offeredSeconds, elapsedSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds,
        missedTicks, skippedHot, skippedCold, cold = cold.Summary(duration), hot = hot.Summary(duration) };
    async Task RemoveWhenDone(long id, Task task) { await task; inFlight.TryRemove(id, out _); }
    async Task Measure(int worker, bool isHot)
    {
        var requestStart = Stopwatch.GetTimestamp();
        var player = players[isHot ? worker % 2 : 2 + worker];
        int status;
        try
        {
            using var response = await Send(clients[(worker / 2) % clients.Length], player, isHot, worker);
            status = (int)response.StatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { status = 0; }
        (isHot ? hot : cold).Add(status, Stopwatch.GetElapsedTime(requestStart).TotalMilliseconds);
    }
}
async Task<HttpResponseMessage> Send(HttpClient client, (string Id, string Token) player, bool gift, int worker)
{
    using var request = new HttpRequestMessage(gift ? HttpMethod.Post : HttpMethod.Get, $"/players/{player.Id}/" + (gift ? "gifts" : "stats"));
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", player.Token);
    if (gift) request.Content = JsonContent.Create(new { toPlayerId = players[1 - worker % 2].Id, points = 1, requestId = Guid.NewGuid() });
    return await client.SendAsync(request);
}
sealed class Measurements
{
    private readonly ConcurrentBag<double> success = new(), rejected = new(), all = new();
    private readonly ConcurrentDictionary<int, int> statuses = new();
    public void Add(int status, double milliseconds)
    {
        all.Add(milliseconds);
        statuses.AddOrUpdate(status, 1, (_, count) => count + 1);
        if (status == 200) success.Add(milliseconds);
        if (status == 429) rejected.Add(milliseconds);
    }
    public object Summary(double duration) => new
    {
        requests = all.Count, successfulRequestsPerSecond = success.Count / duration,
        nonSuccessPercent = all.Count == 0 ? 0 : 100.0 * (all.Count - success.Count) / all.Count,
        throttledPercent = all.Count == 0 ? 0 : 100.0 * rejected.Count / all.Count,
        serverOrTransportFailurePercent = all.Count == 0 ? 0 : 100.0 * statuses.Where(x => x.Key == 0 || x.Key >= 500).Sum(x => x.Value) / all.Count,
        success = Percentiles(success), rejected = Percentiles(rejected), all = Percentiles(all), statuses
    };
    private static object Percentiles(ConcurrentBag<double> samples)
    {
        var values = samples.Order().ToArray();
        double? P(double q) => values.Length == 0 ? null : values[(int)Math.Ceiling(q * values.Length) - 1];
        return new { requests = values.Length, p50Ms = P(.5), p95Ms = P(.95), p99Ms = P(.99) };
    }
}
