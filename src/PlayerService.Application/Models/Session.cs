namespace PlayerService.Application.Models;

public sealed record Session(string SessionToken, int ExpiresInSeconds);
