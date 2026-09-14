using PlayerService.Application.Models;
namespace PlayerService.Application.Abstractions;

public interface IGiftStore
{
    Task<Outcome<GiftReceipt>> ExecuteAsync(string token, string playerId, string toPlayerId, long points, Guid requestId);
}
