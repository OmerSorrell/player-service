namespace PlayerService.Api.Contracts;
public sealed record LoginRequest(string? PlayerId, string? DeviceId);
