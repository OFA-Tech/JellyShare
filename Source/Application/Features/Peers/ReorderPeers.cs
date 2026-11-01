using JellyShare.Application.Features.Admin;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Requests.Peers;
using JellyShare.Domain.Results;

namespace JellyShare.Application.Features.Peers;

/// <summary>Sets the peer priorities: listed peers first in the given order, the others after them in their old order.</summary>
public sealed class ReorderPeersCommandHandler : ICommandHandler<ReorderPeersCommand>
{
    private readonly ISettingsProvider _config;
    private readonly ISettingsStore _store;

    /// <summary>Initializes a new instance of the <see cref="ReorderPeersCommandHandler"/> class.</summary>
    /// <param name="config">The settings.</param>
    /// <param name="store">Persists settings changes.</param>
    public ReorderPeersCommandHandler(ISettingsProvider config, ISettingsStore store)
    {
        _config = config;
        _store = store;
    }

    /// <inheritdoc />
    public Task<Result> Handle(ReorderPeersCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (AdminRules.SettingsLock)
        {
            var cfg = _config.Current;
            var current = AdminRules.ByPriority(cfg.Peers);
            var listed = request.Ids
                .Distinct()
                .Select(id => current.FirstOrDefault(p => p.Id == id))
                .Where(p => p is not null)
                .Select(p => p!)
                .ToList();
            var ordered = listed.Concat(current.Where(p => !listed.Contains(p))).ToList();
            var order = new Dictionary<Guid, int>();
            for (var i = 0; i < ordered.Count; i++)
            {
                order[ordered[i].Id] = i;
            }

            // Copy-on-write: peers are replaced by copies carrying the new priority.
            cfg.Peers = cfg.Peers
                .Select(p => new RemotePeer
                {
                    Id = p.Id,
                    Name = p.Name,
                    BaseUrl = p.BaseUrl,
                    Token = p.Token,
                    Enabled = p.Enabled,
                    Priority = order.TryGetValue(p.Id, out var priority) ? priority : p.Priority,
                })
                .ToList();
            _store.Save(cfg);
        }

        return Task.FromResult(Result.Success());
    }
}