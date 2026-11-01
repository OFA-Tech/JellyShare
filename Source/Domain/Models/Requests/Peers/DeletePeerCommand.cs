using JellyShare.Domain.Interfaces.Messaging;

namespace JellyShare.Domain.Models.Requests.Peers;

/// <summary>Removes a peer and starts a sync so its placeholders go (<c>DELETE admin/peers/{id}</c>).</summary>
/// <param name="Id">The peer id.</param>
public sealed record DeletePeerCommand(Guid Id) : ICommand;
