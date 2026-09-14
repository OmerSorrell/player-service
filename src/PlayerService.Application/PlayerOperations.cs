using System.Text.RegularExpressions;
using PlayerService.Application.Abstractions;
using PlayerService.Application.Models;

namespace PlayerService.Application;

public sealed partial class PlayerOperations(
    ISessionStore sessions, IScoreUpdateStore scores, IGiftStore gifts,
    IPlayerStatsStore stats, ILeaderboardStore leaderboard)
{
    public const long MaximumScore = int.MaxValue;

    [GeneratedRegex("\\A[a-zA-Z0-9_-]{1,64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    public static bool IsIdentifier(string? value) => value is not null && IdentifierPattern().IsMatch(value);
    private static bool IsToken(string? token) => Guid.TryParseExact(token, "N", out var parsed) && parsed != Guid.Empty;
    private static Task<Outcome<T>> Invalid<T>() => Task.FromResult(Outcome<T>.Fail("invalid_input"));
    private static Task<Outcome<T>> Unauthorized<T>() => Task.FromResult(Outcome<T>.Fail("unauthorized"));

    public Task<Outcome<Session>> LoginAsync(string? playerId, string? deviceId) =>
        IsIdentifier(playerId) && IsIdentifier(deviceId) ? sessions.LoginAsync(playerId!, deviceId!) : Invalid<Session>();

    public Task<Outcome<PlayerStats>> StatsAsync(string? token, string? playerId) =>
        !IsToken(token) ? Unauthorized<PlayerStats>() : !IsIdentifier(playerId) ? Invalid<PlayerStats>() : stats.GetAsync(token!, playerId!);

    public Task<Outcome<PlayerStats>> ScoreAsync(string? token, string? playerId, long points, Guid requestId) =>
        !IsToken(token) ? Unauthorized<PlayerStats>() :
        !IsIdentifier(playerId) || points is <= 0 or > MaximumScore || requestId == Guid.Empty
            ? Invalid<PlayerStats>() : scores.AddAsync(token!, playerId!, points, requestId);

    public Task<Outcome<GiftReceipt>> GiftAsync(string? token, string? playerId, string? recipient, long points, Guid requestId) =>
        !IsToken(token) ? Unauthorized<GiftReceipt>() :
        !IsIdentifier(playerId) || !IsIdentifier(recipient) || playerId == recipient || points is <= 0 or > MaximumScore || requestId == Guid.Empty
            ? Invalid<GiftReceipt>() : gifts.ExecuteAsync(token!, playerId!, recipient!, points, requestId);

    public Task<Outcome<LeaderboardEntry[]>> LeaderboardAsync(string? token, int count) =>
        !IsToken(token) ? Unauthorized<LeaderboardEntry[]>() : count is < 1 or > 100
            ? Invalid<LeaderboardEntry[]>() : leaderboard.GetTopAsync(token!, count);
}
