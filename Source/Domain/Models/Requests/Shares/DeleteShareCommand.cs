using JellyShare.Domain.Interfaces.Messaging;

namespace JellyShare.Domain.Models.Requests.Shares;

/// <summary>Revokes a share immediately (<c>DELETE admin/shares/{id}</c>).</summary>
/// <param name="Id">The share id.</param>
public sealed record DeleteShareCommand(Guid Id) : ICommand;
