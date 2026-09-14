using PlayerService.Application.Models;
namespace PlayerService.Application.Abstractions;

public interface ISessionStore
{
    Task<Outcome<Session>> LoginAsync(string playerId, string deviceId);
}
