namespace PlayerService.Api.Contracts;
public sealed record GiftRequest(string? ToPlayerId, long Points, Guid RequestId);
