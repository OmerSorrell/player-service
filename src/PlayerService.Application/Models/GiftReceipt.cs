namespace PlayerService.Application.Models;

public sealed record GiftReceipt(Guid RequestId, PlayerStats Sender, PlayerStats Recipient);
