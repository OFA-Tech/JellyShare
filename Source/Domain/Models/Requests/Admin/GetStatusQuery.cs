using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Admin;

namespace JellyShare.Domain.Models.Requests.Admin;

/// <summary>Gets everything the admin page shows (<c>GET admin/status</c>).</summary>
public sealed record GetStatusQuery : IQuery<StatusResponse>;
