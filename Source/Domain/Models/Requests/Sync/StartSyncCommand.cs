using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Admin;

namespace JellyShare.Domain.Models.Requests.Sync;

/// <summary>Starts a sync in the background unless one is running (<c>POST admin/sync</c>, answered with 202).</summary>
public sealed record StartSyncCommand : ICommand<SyncStartResponse>;
