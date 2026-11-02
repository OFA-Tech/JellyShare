namespace JellyShare.Data.Peers.Clients;

/// <summary>The fields of <c>POST /Users/AuthenticateByName</c> this plugin reads.</summary>
/// <param name="AccessToken">The access token.</param>
/// <param name="User">The signed-in user.</param>
internal sealed record JfAuthResult(string? AccessToken, JfUser? User);

/// <summary>The fields of a Jellyfin user this plugin reads.</summary>
/// <param name="Id">The user id.</param>
/// <param name="Policy">The user policy (from <c>GET /Users/Me</c>).</param>
internal sealed record JfUser(string? Id, JfPolicy? Policy);

/// <summary>The fields of a Jellyfin user policy this plugin reads.</summary>
/// <param name="RemoteClientBitrateLimit">The remote streaming limit in bit/s; 0 means none.</param>
internal sealed record JfPolicy(int? RemoteClientBitrateLimit);

/// <summary>The fields of <c>GET /System/Info/Public</c> this plugin reads.</summary>
/// <param name="ServerName">The server name.</param>
internal sealed record JfPublicInfo(string? ServerName);

/// <summary>One page of <c>GET /Items</c>.</summary>
/// <param name="Items">The items of the page.</param>
/// <param name="TotalRecordCount">The item count over all pages.</param>
internal sealed record JfItemsPage(List<JfItem?>? Items, int TotalRecordCount);

/// <summary>The fields of a Jellyfin item this plugin reads.</summary>
/// <param name="Id">The item id.</param>
/// <param name="Type">The item type, e.g. <c>Movie</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="ProductionYear">The production year.</param>
/// <param name="ProviderIds">External provider ids.</param>
/// <param name="SeriesName">The series name of an episode.</param>
/// <param name="SeriesId">The series id of an episode.</param>
/// <param name="ParentIndexNumber">The season number of an episode.</param>
/// <param name="IndexNumber">The episode number.</param>
/// <param name="RunTimeTicks">The runtime in ticks.</param>
/// <param name="Overview">The plot summary.</param>
/// <param name="LocationType">Where the item lives; <c>Virtual</c> items have no file.</param>
/// <param name="MediaSources">The media sources.</param>
/// <param name="ImageTags">The image types with their tags.</param>
/// <param name="BackdropImageTags">The backdrop image tags.</param>
/// <param name="DateCreated">When the item was added.</param>
internal sealed record JfItem(
    string? Id,
    string? Type,
    string? Name,
    int? ProductionYear,
    Dictionary<string, string?>? ProviderIds,
    string? SeriesName,
    string? SeriesId,
    int? ParentIndexNumber,
    int? IndexNumber,
    long? RunTimeTicks,
    string? Overview,
    string? LocationType,
    List<JfMediaSource?>? MediaSources,
    Dictionary<string, string?>? ImageTags,
    List<string?>? BackdropImageTags,
    DateTime? DateCreated);

/// <summary>The fields of a Jellyfin media source this plugin reads.</summary>
/// <param name="Protocol">The source protocol; only <c>File</c> sources are shared.</param>
/// <param name="Container">The container format.</param>
/// <param name="Bitrate">The overall bit rate.</param>
/// <param name="MediaStreams">The video, audio and subtitle streams.</param>
internal sealed record JfMediaSource(string? Protocol, string? Container, int? Bitrate, List<JfMediaStream?>? MediaStreams);

/// <summary>The fields of a Jellyfin media stream this plugin reads.</summary>
/// <param name="Type"><c>Video</c>, <c>Audio</c> or <c>Subtitle</c>.</param>
/// <param name="Codec">The codec.</param>
/// <param name="Language">The language code.</param>
/// <param name="Width">The video width.</param>
/// <param name="Height">The video height.</param>
/// <param name="Channels">The audio channel count.</param>
/// <param name="DisplayTitle">The display title.</param>
internal sealed record JfMediaStream(string? Type, string? Codec, string? Language, int? Width, int? Height, int? Channels, string? DisplayTitle);
