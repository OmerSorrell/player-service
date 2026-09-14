using PlayerService.Application.Models;
namespace PlayerService.Application.Abstractions;

public interface IScoreUpdateStore
{
    Task<Outcome<PlayerStats>> AddAsync(string token, string playerId, long points, Guid requestId);
}
