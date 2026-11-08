using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Admin;
using JellyShare.Domain.Models.Requests.Admin;
using JellyShare.Domain.Models.Requests.Peers;
using JellyShare.Domain.Models.Requests.Shares;
using JellyShare.Domain.Models.Requests.Sync;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JellyShare.Jellyfin.Controllers.V1;

/// <summary>
/// Admin-only endpoints behind the JellyShare configuration page. The whole controller requires an administrator;
/// nothing here is anonymous. Tokens are only ever returned by <see cref="CreateShare"/> (once) and never logged.
/// The use cases live in <c>JellyShare.Application.Features</c>; this maps HTTP to them.
/// </summary>
[ApiController]
[Route("JellyShare/v1/admin")]
[Authorize(Policy = Policies.RequiresElevation)]
public class AdminController : JellyShareControllerBase
{
    private const string MissingBody = "Missing request body.";

    /// <summary>Initializes a new instance of the <see cref="AdminController"/> class.</summary>
    /// <param name="sender">Dispatches the admin use cases.</param>
    public AdminController(ISender sender)
        : base(sender)
    {
    }

    /// <summary>Gets everything the admin page shows.</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The status.</returns>
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct = default)
        => ToActionResult(await Sender.Send(new GetStatusQuery(), ct).ConfigureAwait(false));

    /// <summary>Creates a share and returns its token once.</summary>
    /// <param name="request">The share.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created share with the plaintext token.</returns>
    [HttpPost("shares")]
    public async Task<IActionResult> CreateShare([FromBody] CreateShareRequest request, CancellationToken ct = default)
    {
        if (request is null)
        {
            return BadRequestError(MissingBody);
        }

        var result = await Sender.Send(new CreateShareCommand(request.Name, request.LibraryIds, request.MaxStreams, request.MaxBitrate, request.UseDefaultTypes, request.Types), ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            Response.Headers.CacheControl = "no-store";
        }

        return ToActionResult(result);
    }

    /// <summary>Updates a share's name, media types, limits and enabled flag.</summary>
    /// <param name="id">The share id.</param>
    /// <param name="request">The new values.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated share.</returns>
    [HttpPut("shares/{id:guid}")]
    public async Task<IActionResult> UpdateShare(Guid id, [FromBody] UpdateShareRequest request, CancellationToken ct = default)
    {
        if (request is null)
        {
            return BadRequestError(MissingBody);
        }

        return ToActionResult(await Sender.Send(
            new UpdateShareCommand(id, request.Name, request.LibraryIds, request.MaxStreams, request.MaxBitrate, request.Enabled, request.UseDefaultTypes, request.Types),
            ct).ConfigureAwait(false));
    }

    /// <summary>Sets the media types a share offers by default.</summary>
    /// <param name="request">The default sharing.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The default sharing now in effect.</returns>
    [HttpPut("sharing/default")]
    public async Task<IActionResult> UpdateDefaultSharing([FromBody] DefaultSharingRequest request, CancellationToken ct = default)
    {
        if (request is null)
        {
            return BadRequestError(MissingBody);
        }

        return ToActionResult(await Sender.Send(new UpdateDefaultSharingCommand(request.All, request.Types), ct).ConfigureAwait(false));
    }

    /// <summary>Revokes a share immediately.</summary>
    /// <param name="id">The share id.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>No content, or 404.</returns>
    [HttpDelete("shares/{id:guid}")]
    public async Task<IActionResult> DeleteShare(Guid id, CancellationToken ct = default)
        => ToActionResult(await Sender.Send(new DeleteShareCommand(id), ct).ConfigureAwait(false));

    /// <summary>Adds a peer this server receives from.</summary>
    /// <param name="request">The peer.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created peer.</returns>
    [HttpPost("peers")]
    public async Task<IActionResult> AddPeer([FromBody] AddPeerRequest request, CancellationToken ct = default)
    {
        if (request is null)
        {
            return BadRequestError(MissingBody);
        }

        return ToActionResult(await Sender.Send(
            new AddPeerCommand(request.Name, request.BaseUrl, request.Token, request.Enabled, request.Kind, request.Username, request.Password, request.MaxStreams, request.LimitAction),
            ct).ConfigureAwait(false));
    }

    /// <summary>Updates a peer.</summary>
    /// <param name="id">The peer id.</param>
    /// <param name="request">The new values.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated peer.</returns>
    [HttpPut("peers/{id:guid}")]
    public async Task<IActionResult> UpdatePeer(Guid id, [FromBody] UpdatePeerRequest request, CancellationToken ct = default)
    {
        if (request is null)
        {
            return BadRequestError(MissingBody);
        }

        return ToActionResult(await Sender.Send(
            new UpdatePeerCommand(id, request.Name, request.BaseUrl, request.Token, request.Enabled, request.Kind, request.Username, request.Password, request.MaxStreams, request.LimitAction),
            ct).ConfigureAwait(false));
    }

    /// <summary>Removes a peer.</summary>
    /// <param name="id">The peer id.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>No content, or 404.</returns>
    [HttpDelete("peers/{id:guid}")]
    public async Task<IActionResult> DeletePeer(Guid id, CancellationToken ct = default)
        => ToActionResult(await Sender.Send(new DeletePeerCommand(id), ct).ConfigureAwait(false));

    /// <summary>Sets the peer priority order.</summary>
    /// <param name="request">The ids, most preferred first.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpPost("peers/order")]
    public async Task<IActionResult> ReorderPeers([FromBody] ReorderPeersRequest request, CancellationToken ct = default)
    {
        if (request?.Ids is null)
        {
            return BadRequestError("Missing peer ids.");
        }

        return ToActionResult(await Sender.Send(new ReorderPeersCommand(request.Ids), ct).ConfigureAwait(false));
    }

    /// <summary>Tests a peer URL and token: ping, then catalog fetch.</summary>
    /// <param name="request">The URL and token.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The outcome (always 200; errors are in the body and never contain the token).</returns>
    [HttpPost("peers/test")]
    public async Task<IActionResult> TestPeer([FromBody] TestPeerRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequestError(MissingBody);
        }

        return ToActionResult(await Sender.Send(
            new TestPeerQuery(request.Url, request.Token, request.PeerId, request.Kind, request.Username, request.Password),
            ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Starts a sync in the background (not tied to the request: a first sync with many images can take minutes) and
    /// returns at once. The page polls <c>admin/status</c> (<c>syncRunning</c>, <c>lastSync</c>, <c>syncError</c>).
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>202 with whether a sync was started or one was already running.</returns>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync(CancellationToken ct = default)
    {
        var result = await Sender.Send(new StartSyncCommand(), ct).ConfigureAwait(false);
        return result.IsSuccess ? StatusCode(StatusCodes.Status202Accepted, result.Value) : ErrorResult(result.Error);
    }

    /// <summary>Adds one of the plugin's own placeholder roots to a library and refreshes it.</summary>
    /// <param name="request">The library and which root.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The placeholder root state.</returns>
    [HttpPost("libraries/attach")]
    public async Task<IActionResult> Attach([FromBody] AttachLibraryRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequestError(MissingBody);
        }

        return ToActionResult(await Sender.Send(new AttachLibraryCommand(request.LibraryId, request.Type), ct).ConfigureAwait(false));
    }
}
