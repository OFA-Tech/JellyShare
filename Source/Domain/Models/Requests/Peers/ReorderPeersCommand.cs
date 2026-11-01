using JellyShare.Domain.Interfaces.Messaging;

namespace JellyShare.Domain.Models.Requests.Peers;

/// <summary>Sets the peer priority order (<c>POST admin/peers/order</c>).</summary>
/// <param name="Ids">The peer ids, most preferred first; unlisted peers keep their order after them.</param>
public sealed record ReorderPeersCommand(IReadOnlyList<Guid> Ids) : ICommand;
