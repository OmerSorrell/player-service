using PlayerService.Application.Models;
namespace PlayerService.Application.Abstractions;

public interface ILeaderboardStore
{
    Task<Outcome<LeaderboardEntry[]>> GetTopAsync(string token, int count);
}
