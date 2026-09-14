using PlayerService.Application.Models;
namespace PlayerService.Application.Abstractions;

public interface IPlayerStatsStore
{
    Task<Outcome<PlayerStats>> GetAsync(string token, string playerId);
}
