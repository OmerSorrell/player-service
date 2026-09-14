namespace PlayerService.Application.Models;

public sealed record Outcome<T>(T? Value, string? Error = null, bool IsReplay = false)
{
    public bool IsSuccess => Error is null;
    public static Outcome<T> Fail(string error) => new(default, error);
}
