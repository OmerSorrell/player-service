namespace PlayerService.Application.Models;

public sealed record PlayerStats(string PlayerId, long Score, long GiftsSent, long GiftsReceived, DateTimeOffset LastActive);
