using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyShare.Protocol;

/// <summary>
/// Wire protocol constants shared by the provider and consumer sides.
/// </summary>
public static class ProtocolInfo
{
    /// <summary>The federation protocol version served under <c>/JellyShare/v1</c>.</summary>
    public const string Version = "1.0";
}

/// <summary>
/// Response of the anonymous <c>GET /JellyShare/v1/ping</c> endpoint.
/// </summary>
/// <param name="ProtocolVersion">The protocol version the server speaks.</param>
/// <param name="ServerName">The friendly name of the Jellyfin server.</param>
public sealed record PingResponse(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("serverName")] string ServerName);

/// <summary>Short JSON error body of the federation endpoints.</summary>
/// <param name="Error">A short, fixed error description.</param>
public sealed record ErrorResponse([property: JsonPropertyName("error")] string Error);

/// <summary>
/// Response of <c>GET /JellyShare/v1/catalog</c>.
/// </summary>
/// <param name="ProtocolVersion">The protocol version the server speaks.</param>
/// <param name="ServerName">The friendly name of the Jellyfin server.</param>
/// <param name="Etag">Opaque hash of the catalog content (also sent as the <c>ETag</c> header).</param>
/// <param name="Items">The shared movies and episodes.</param>
public sealed record CatalogResponse(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("serverName")] string ServerName,
    [property: JsonPropertyName("etag")] string Etag,
    [property: JsonPropertyName("items")] IReadOnlyList<CatalogItem> Items);

/// <summary>An audio track of a catalog item.</summary>
/// <param name="Language">ISO language code, if known.</param>
/// <param name="Codec">The audio codec.</param>
/// <param name="Channels">The channel count.</param>
/// <param name="Title">The track title or display name.</param>
public sealed record AudioTrack(
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("codec")] string? Codec,
    [property: JsonPropertyName("channels")] int? Channels,
    [property: JsonPropertyName("title")] string? Title);

/// <summary>A subtitle track of a catalog item.</summary>
/// <param name="Language">ISO language code, if known.</param>
/// <param name="Codec">The subtitle codec.</param>
/// <param name="Title">The track title or display name.</param>
public sealed record SubtitleTrack(
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("codec")] string? Codec,
    [property: JsonPropertyName("title")] string? Title);

/// <summary>Technical media information of a catalog item.</summary>
/// <param name="Container">The container format.</param>
/// <param name="VideoCodec">The video codec.</param>
/// <param name="Width">Video width in pixels.</param>
/// <param name="Height">Video height in pixels.</param>
/// <param name="BitRate">Overall bit rate in bits per second.</param>
/// <param name="Audio">The audio tracks.</param>
/// <param name="Subtitles">The subtitle tracks.</param>
public sealed record MediaInfo(
    [property: JsonPropertyName("container")] string? Container,
    [property: JsonPropertyName("videoCodec")] string? VideoCodec,
    [property: JsonPropertyName("width")] int? Width,
    [property: JsonPropertyName("height")] int? Height,
    [property: JsonPropertyName("bitRate")] int? BitRate,
    [property: JsonPropertyName("audio")] AudioTrack[] Audio,
    [property: JsonPropertyName("subtitles")] SubtitleTrack[] Subtitles);

/// <summary>A movie or episode in a provider's shared catalog.</summary>
/// <param name="Id">The item id on the providing server.</param>
/// <param name="Type"><c>Movie</c> or <c>Episode</c>.</param>
/// <param name="Name">The item name.</param>
/// <param name="Year">The production year.</param>
/// <param name="ProviderIds">External provider ids (Tmdb, Imdb, Tvdb).</param>
/// <param name="SeriesName">Series name for episodes.</param>
/// <param name="SeriesProviderIds">External provider ids of the series for episodes.</param>
/// <param name="SeasonNumber">Season number for episodes.</param>
/// <param name="EpisodeNumber">Episode number for episodes.</param>
/// <param name="RunTimeTicks">Runtime in ticks.</param>
/// <param name="Overview">The plot summary.</param>
/// <param name="Media">Technical media information.</param>
/// <param name="ImageTypes">Image types available on the item.</param>
/// <param name="DateModified">Last modification time.</param>
/// <param name="SeriesId">The item id of the series on the providing server, used to fetch series artwork.</param>
public sealed record CatalogItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("providerIds")] Dictionary<string, string> ProviderIds,
    [property: JsonPropertyName("seriesName")] string? SeriesName,
    [property: JsonPropertyName("seriesProviderIds")] Dictionary<string, string>? SeriesProviderIds,
    [property: JsonPropertyName("seasonNumber")] int? SeasonNumber,
    [property: JsonPropertyName("episodeNumber")] int? EpisodeNumber,
    [property: JsonPropertyName("runTimeTicks")] long? RunTimeTicks,
    [property: JsonPropertyName("overview")] string? Overview,
    [property: JsonPropertyName("media")] MediaInfo Media,
    [property: JsonPropertyName("imageTypes")] string[] ImageTypes,
    [property: JsonPropertyName("dateModified")] DateTime DateModified,
    [property: JsonPropertyName("seriesId")] string? SeriesId = null);
