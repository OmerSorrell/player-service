using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

var urls = (args.ElementAtOrDefault(0) ?? "http://localhost:5080").Split(',');
var seconds = int.Parse(args.ElementAtOrDefault(1) ?? "5");
var hotWorkers = int.Parse(args.ElementAtOrDefault(2) ?? "32");
if (seconds is < 1 or > 120 || hotWorkers is < 1 or > 256)
    throw new ArgumentException("Duration must be 1–120 seconds; hot workers 1–256.");
var clients = urls.Select(url => new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) }).ToArray();
var run = "load_" + Guid.NewGuid().ToString("N")[..12];
var players = new List<(string Id, string Token)>();
for (var i = 0; i < 34; i++)
{
    var id = $"{run}_{i}";
    using var response = await clients[i % clients.Length].PostAsJsonAsync("/login", new { playerId = id, deviceId = id });
    response.EnsureSuccessStatusCode();
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    players.Add((id, json.RootElement.GetProperty("sessionToken").GetString()!));
}

// Warm both API hosts and the script cache before measuring.
for (var i = 0; i < 100; i++) await Request(i, false);
var baseline = await Phase("cold_only", 0);
var mixed = await Phase("hot_gifts_and_cold_reads", hotWorkers);
long total = 0;
foreach (var player in players)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, $"/players/{player.Id}/stats");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", player.Token);
    using var response = await clients[0].SendAsync(request);
    response.EnsureSuccessStatusCode();
    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var score = body.RootElement.GetProperty("score").GetInt64();
    if (score < 0) throw new InvalidOperationException("Negative balance!");
    total += score;
}
if (total != players.Count * 1000) throw new InvalidOperationException($"Conservation failed: {total}");
var output = JsonSerializer.Serialize(new
{
    timestamp = DateTimeOffset.UtcNow, urls, seconds, hotWorkers, run,
    baseline, mixed, conservedPoints = total,
    note = "Closed-loop local diagnostic, not a production capacity or strict latency-isolation guarantee. Latencies and throughput include rejected requests; statuses combine hot and cold traffic."
}, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(output);
if (args.Length > 3)
{
    var path = Path.GetFullPath(args[3]);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllTextAsync(path, output);
}
foreach (var client in clients) client.Dispose();

async Task<object> Phase(string name, int writers)
{
    var cold = new ConcurrentBag<double>();
    var hot = new ConcurrentBag<double>();
    var statuses = new ConcurrentDictionary<int, int>();
    var start = Stopwatch.GetTimestamp();
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var tasks = Enumerable.Range(0, writers + 4).Select(async worker =>
    {
        await gate.Task;
        var isHot = worker >= 4;
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < seconds)
        {
            var requestStart = Stopwatch.GetTimestamp();
            var status = await Request(worker, isHot);
            statuses.AddOrUpdate(status, 1, (_, value) => value + 1);
            (isHot ? hot : cold).Add(Stopwatch.GetElapsedTime(requestStart).TotalMilliseconds);
        }
    }).ToArray();
    start = Stopwatch.GetTimestamp();
    gate.SetResult();
    await Task.WhenAll(tasks);
    var elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
    return new { name, elapsedSeconds = elapsed, requestsPerSecond = (cold.Count + hot.Count) / elapsed,
        cold = Summary(cold), hot = Summary(hot), statuses };
}

async Task<int> Request(int worker, bool gift)
{
    var player = players[gift ? worker % 2 : 2 + worker % 32];
    using var request = new HttpRequestMessage(gift ? HttpMethod.Post : HttpMethod.Get,
        $"/players/{player.Id}/" + (gift ? "gifts" : "stats"));
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", player.Token);
    if (gift) request.Content = JsonContent.Create(new
    {
        toPlayerId = players[1 - worker % 2].Id, points = 1, requestId = Guid.NewGuid()
    });
    try
    {
        using var response = await clients[worker % clients.Length].SendAsync(request);
        return (int)response.StatusCode;
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { return 0; }
}

static object Summary(ConcurrentBag<double> samples)
{
    var values = samples.Order().ToArray();
    double Percentile(double q) => values.Length == 0 ? 0 : values[(int)Math.Ceiling(q * values.Length) - 1];
    return new { requests = values.Length, p50Ms = Percentile(0.5), p95Ms = Percentile(0.95), p99Ms = Percentile(0.99) };
}
