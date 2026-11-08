using System.Security.Cryptography;
using System.Text;
using JellyShare.Application.Features.Placeholders;
using JellyShare.Domain.Interfaces.Relay;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.DependencyInjection;
using AudioItem = MediaBrowser.Controller.Entities.Audio.Audio;

namespace JellyShare.Jellyfin.Adapters;

/// <summary>
/// Jellyfin 12.4 keeps the <c>.strm</c> file itself as the media source of an audio item (the ShortcutPath swap is
/// video-only), so this adds an Http, remote source at the relay URL for music and audiobook placeholders; the
/// PlaybackInfo filter points stream requests at it (docs/spike-results.md, audio playback spike). Jellyfin finds it
/// through <c>GetExports&lt;IMediaSourceProvider&gt;()</c>, so it must stay public.
/// </summary>
public sealed class AudioRelaySourceProvider : IMediaSourceProvider
{
    private readonly IServiceProvider _services;

    /// <summary>Initializes a new instance of the <see cref="AudioRelaySourceProvider"/> class.</summary>
    /// <param name="services">Jellyfin's service provider (plugin services are internal, so they are resolved on use).</param>
    public AudioRelaySourceProvider(IServiceProvider services) => _services = services;

    /// <inheritdoc />
    public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is not AudioItem
            || _services.GetService<IPlaceholderLookup>()?.Find(item.Id) is not { IsAudio: true } placeholder
            || _services.GetService<IRelayBase>() is not { } relayBase)
        {
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        var stored = _services.GetService<IMediaSourceManager>()?.GetMediaStreams(item.Id) ?? [];
        return Task.FromResult<IEnumerable<MediaSourceInfo>>([Source(item, placeholder, relayBase.Current, stored)]);
    }

    /// <inheritdoc />
    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    /// <summary>The media source id of an item's relay source (stable, and not the item id).</summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The media source id ("N" format).</returns>
    internal static string SourceId(Guid itemId)
        => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("jellyshare-relay:" + itemId.ToString("N"))).AsSpan(0, 16)).ToString("N");

    /// <summary>Builds the relay source of an audio placeholder.</summary>
    /// <param name="item">The library item.</param>
    /// <param name="placeholder">The placeholder.</param>
    /// <param name="relayBase">The current relay base URL.</param>
    /// <param name="stored">The media streams Jellyfin stored for the item.</param>
    /// <returns>The source.</returns>
    internal static MediaSourceInfo Source(BaseItem item, PlaceholderMedia placeholder, string relayBase, IReadOnlyList<MediaStream> stored)
    {
        var media = placeholder.Item?.Media;
        var container = string.IsNullOrEmpty(item.Container) || string.Equals(item.Container, "strm", StringComparison.OrdinalIgnoreCase)
            ? media?.Container
            : item.Container;
        // Jellyfin stores no container for audiobooks of a multi-file folder, on either server; without one, universal
        // matches no direct-play profile and sends the client to HLS.
        // shortcut: the codec name stands in for the container (exact for mp3 and flac, "aac" for an m4b), until providers send the file extension.
        container ??= stored.FirstOrDefault(s => s.Type == MediaStreamType.Audio)?.Codec ?? media?.Audio.FirstOrDefault()?.Codec;
        var streams = stored.ToList();
        if (!streams.Any(s => s.Type == MediaStreamType.Audio))
        {
            // Multi-file audiobook folders store no streams (Jellyfin probes the .strm text), and without an audio stream
            // PlaybackInfo and universal fail with 400.
            var track = media?.Audio.FirstOrDefault();
            streams.Add(new MediaStream
            {
                Type = MediaStreamType.Audio,
                Index = 0,
                Codec = track?.Codec ?? container,
                Channels = track?.Channels,
                BitRate = placeholder.SourceBitrate,
                IsDefault = true,
            });
        }

        return new MediaSourceInfo
        {
            Id = SourceId(item.Id),
            Name = item.Name,
            Path = PlaceholderPaths.StrmContent(relayBase, placeholder.CanonicalKey),
            Protocol = MediaProtocol.Http,
            IsRemote = true,
            Type = MediaSourceType.Default,
            Container = container,
            // Without a runtime the HLS master points at Audio/{id}/live.m3u8, which Jellyfin does not serve.
            RunTimeTicks = item.RunTimeTicks ?? placeholder.Item?.RunTimeTicks,
            Bitrate = placeholder.SourceBitrate,
            MediaStreams = streams,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            SupportsProbing = true,
            IsInfiniteStream = false,
            RequiresOpening = false,
            RequiresClosing = false,
            ETag = item.Id.ToString("N"),
        };
    }
}
