using JellyShare.Application.Features.Admin;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Interfaces.Peers;
using JellyShare.Domain.Models.Admin;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Peers;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Models.Requests.Peers;
using JellyShare.Domain.Results;

namespace JellyShare.Application.Features.Peers;

/// <summary>
/// Tests a peer URL and token: ping, then catalog fetch. Always a success: what went wrong is in the response, in words
/// the admin can act on, and never contains the token. A saved peer's token is only ever sent to its saved address.
/// </summary>
public sealed class TestPeerQueryHandler : IQueryHandler<TestPeerQuery, TestPeerResponse>
{
    private const string AddressChangedMessage =
        "The address was changed. Enter the token again to test the new address (the saved token is only used for the saved address).";

    private const string RedirectMessage =
        "The server answered with a redirect, which is refused for security. Use the final address (check http vs https and the path).";

    private readonly ISettingsProvider _config;
    private readonly IPeerClient _peers;

    /// <summary>Initializes a new instance of the <see cref="TestPeerQueryHandler"/> class.</summary>
    /// <param name="config">The settings (saved peers).</param>
    /// <param name="peers">The peer client.</param>
    public TestPeerQueryHandler(ISettingsProvider config, IPeerClient peers)
    {
        _config = config;
        _peers = peers;
    }

    /// <inheritdoc />
    public async Task<Result<TestPeerResponse>> Handle(TestPeerQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var url = AdminRules.NormalizeUrl(request.Url);
        if (url is null)
        {
            return Fail(AdminRules.InvalidUrlMessage);
        }

        var token = request.Token?.Trim() ?? string.Empty;
        var peerId = Guid.Empty;
        if (token.Length == 0 && request.PeerId is { } savedId)
        {
            var saved = _config.Current.Peers.FirstOrDefault(p => p.Id == savedId);
            if (saved is not null)
            {
                // The stored token only ever goes to the address it was saved for; a changed address needs the token typed again.
                if (!string.Equals(url, AdminRules.NormalizeUrl(saved.BaseUrl), StringComparison.Ordinal))
                {
                    return Fail(AddressChangedMessage);
                }

                token = saved.Token ?? string.Empty;
                peerId = saved.Id;
            }
        }

        if (token.Length == 0)
        {
            return Fail(AdminRules.MissingTokenMessage);
        }

        if (AdminRules.TokenError(token) is { } tokenError)
        {
            return Fail(tokenError);
        }

        var ping = await _peers.PingAsync(url, cancellationToken).ConfigureAwait(false);
        if (!ping.Ok)
        {
            return Fail(Redact(PingMessage(ping), token));
        }

        var fetch = await _peers.FetchCatalogAsync(
            new RemotePeer { Id = peerId, Name = "test", BaseUrl = url, Token = token },
            null,
            cancellationToken).ConfigureAwait(false);
        if (!fetch.Ok || fetch.Catalog is null)
        {
            return Fail(Redact(CatalogMessage(fetch.Error), token));
        }

        return Result.Success(new TestPeerResponse(
            true,
            string.IsNullOrWhiteSpace(ping.ServerName) ? fetch.Catalog.ServerName : ping.ServerName,
            fetch.Catalog.ProtocolVersion,
            fetch.Catalog.Items.Count,
            null));
    }

    /// <summary>Explains a failed ping.</summary>
    /// <param name="ping">The ping result.</param>
    /// <returns>The message.</returns>
    public static string PingMessage(PeerPingResult ping)
    {
        ArgumentNullException.ThrowIfNull(ping);
        return ping.Failure switch
        {
            PingFailure.InvalidUrl => AdminRules.InvalidUrlMessage,
            PingFailure.Unreachable => "Could not reach the server" + (string.IsNullOrWhiteSpace(ping.Detail) ? "." : ": " + ping.Detail),
            PingFailure.Timeout => "The server did not answer in time.",
            PingFailure.Redirect => RedirectMessage,
            PingFailure.IncompatibleProtocol => $"Incompatible JellyShare protocol version {ping.ProtocolVersion}: this server speaks {ProtocolInfo.Version}. Update the plugin on the older server.",
            _ => "That address is not a JellyShare server (or the JellyShare plugin is not installed there)."
                + (string.IsNullOrWhiteSpace(ping.Detail) ? string.Empty : " (" + ping.Detail + ")"),
        };
    }

    /// <summary>Explains a failed catalog fetch.</summary>
    /// <param name="error">The peer client's error.</param>
    /// <returns>The message.</returns>
    public static string CatalogMessage(string? error)
    {
        var e = error ?? string.Empty;
        if (e.StartsWith("HTTP 401", StringComparison.Ordinal))
        {
            return "The server rejected the token (HTTP 401). Check that the token is complete and still valid, or ask for a new invitation.";
        }

        if (e.StartsWith("HTTP 403", StringComparison.Ordinal))
        {
            return "The server answered HTTP 403: a reverse proxy or firewall in front of it may be blocking JellyShare requests.";
        }

        if (e.StartsWith("HTTP 3", StringComparison.Ordinal))
        {
            return RedirectMessage;
        }

        if (e.StartsWith("timeout", StringComparison.Ordinal))
        {
            return "The server did not answer the catalog request in time.";
        }

        if (e.StartsWith("incompatible protocol", StringComparison.Ordinal))
        {
            return "Incompatible JellyShare protocol (" + e["incompatible protocol".Length..].Trim() + "). Update the plugin on the older server.";
        }

        if (e.StartsWith("invalid catalog", StringComparison.Ordinal))
        {
            return "The server answered, but the catalog is not valid JellyShare data.";
        }

        if (e.StartsWith("connection failed", StringComparison.Ordinal))
        {
            return "Could not connect to the server" + e["connection failed".Length..];
        }

        return "The catalog request failed: " + (string.IsNullOrWhiteSpace(e) ? "unknown error" : e);
    }

    private static string Redact(string message, string token)
        => string.IsNullOrEmpty(token) ? message : message.Replace(token, "***", StringComparison.Ordinal);

    private static Result<TestPeerResponse> Fail(string error) => Result.Success(new TestPeerResponse(false, null, null, null, error));
}