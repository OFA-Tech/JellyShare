using JellyShare.Application.Features.Streaming;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Streaming;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JellyShare.Jellyfin.Controllers.V1;

/// <summary>
/// The HTTP side of a capped stream: sets the headers, lets <see cref="CappedStreamService.StreamAsync"/> write the
/// transcode into the response body and maps how it ended to the response (503 when nothing was served, a cut
/// connection when a newer request took over). Releases the transcode slot and the stream session at the end.
/// </summary>
internal sealed class CappedStreamResult : IActionResult
{
    private readonly CappedStreamService _service;
    private readonly EncoderSettings _settings;
    private readonly string _inputPath;
    private readonly long _cap;
    private readonly double? _start;
    private readonly StreamLease _lease;
    private readonly TranscodeSlot? _slot;

    /// <summary>Initializes a new instance of the <see cref="CappedStreamResult"/> class.</summary>
    /// <param name="service">The capped stream service.</param>
    /// <param name="settings">Settings from <see cref="CappedStreamService.Prepare"/>.</param>
    /// <param name="inputPath">The item's file (from Jellyfin, never from the request).</param>
    /// <param name="cap">The cap in bits per second.</param>
    /// <param name="start">Where to start, or <c>null</c>.</param>
    /// <param name="lease">The stream session, released when the transcode ends.</param>
    /// <param name="slot">The reserved transcode, or <c>null</c> for HEAD (headers only).</param>
    public CappedStreamResult(CappedStreamService service, EncoderSettings settings, string inputPath, long cap, double? start, StreamLease lease, TranscodeSlot? slot)
    {
        _service = service;
        _settings = settings;
        _inputPath = inputPath;
        _cap = cap;
        _start = start;
        _lease = lease;
        _slot = slot;
    }

    /// <inheritdoc />
    public async Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var http = context.HttpContext;
        var response = http.Response;
        try
        {
            // Headers are only set here; they go out with the first byte, so a failure before any output can
            // still become a 503 and the consumer fails over to another peer.
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "video/mp2t";
            response.Headers.AcceptRanges = "none";
            response.Headers.CacheControl = "no-store";
            if (_slot is null)
            {
                return;
            }

            var outcome = await _service.StreamAsync(_settings, _inputPath, _cap, _start, _slot, response.Body, http.RequestAborted).ConfigureAwait(false);
            switch (outcome)
            {
                case CappedStreamOutcome.FailedWithoutOutput:
                    Fail(http);
                    break;
                case CappedStreamOutcome.Superseded:
                    http.Abort();
                    break;
            }
        }
        finally
        {
            _slot?.Dispose();
            _lease.Dispose();
        }
    }

    private void Fail(HttpContext http)
    {
        if (http.Response.HasStarted)
        {
            return;
        }

        http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        http.Response.Headers.Remove("Accept-Ranges");
        http.Response.Headers.Remove("Cache-Control");
        http.Response.ContentType = null;

        // Nothing was served: a session this request opened gives its slot back at once.
        _lease.Abort();
    }
}
