using PlayerService.Api.Contracts;
using PlayerService.Application;
using PlayerService.Application.Models;

namespace PlayerService.Api.Endpoints;

public static class PlayerEndpoints
{
    public static void MapPlayerEndpoints(this WebApplication app)
    {
        app.MapPost("/login", async (LoginRequest body, HttpContext context, PlayerOperations service) =>
            Map(await service.LoginAsync(body.PlayerId, body.DeviceId).WaitAsync(context.RequestAborted)));
        app.MapGet("/players/{playerId}/stats", async (string playerId, HttpContext context, PlayerOperations service) =>
            Map(await service.StatsAsync(Token(context), playerId).WaitAsync(context.RequestAborted)));
        app.MapPost("/players/{playerId}/stats/score", async (string playerId, ScoreRequest body, HttpContext context, PlayerOperations service) =>
            Map(await service.ScoreAsync(Token(context), playerId, body.Points, body.RequestId).WaitAsync(context.RequestAborted)));
        app.MapPost("/players/{playerId}/gifts", async (string playerId, GiftRequest body, HttpContext context, PlayerOperations service) =>
            Map(await service.GiftAsync(Token(context), playerId, body.ToPlayerId, body.Points, body.RequestId).WaitAsync(context.RequestAborted)));
        app.MapGet("/leaderboard", async (HttpContext context, PlayerOperations service) =>
        {
            var raw = context.Request.Query["n"];
            if (raw.Count > 1 || (raw.Count == 1 && !int.TryParse(raw[0], out _)))
                return Map(Outcome<LeaderboardEntry[]>.Fail("invalid_input"));
            return Map(await service.LeaderboardAsync(Token(context), raw.Count == 0 ? 10 : int.Parse(raw[0]!)).WaitAsync(context.RequestAborted));
        });
    }

    private static string? Token(HttpContext context)
    {
        var header = context.Request.Headers.Authorization;
        if (header.Count != 1) return null;
        var value = header[0]!;
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..].Trim() : null;
    }

    private static IResult Map<T>(Outcome<T> outcome)
    {
        if (outcome.IsSuccess) return Results.Ok(outcome.Value);
        var status = outcome.Error switch
        {
            "unauthorized" => 401,
            "forbidden" => 403,
            "player_not_found" => 404,
            "invalid_input" => 422,
            _ => 409
        };
        return Results.Json(new { error = outcome.Error }, statusCode: status);
    }
}
