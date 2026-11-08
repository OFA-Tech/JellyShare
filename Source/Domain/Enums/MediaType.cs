namespace JellyShare.Domain.Enums;

/// <summary>A kind of media a share can offer and a peer can accept.</summary>
public enum MediaType
{
    /// <summary>Movies.</summary>
    Movies = 0,

    /// <summary>TV shows (series, seasons and episodes).</summary>
    Shows = 1,

    /// <summary>Music (tracks).</summary>
    Music = 2,

    /// <summary>Audiobooks.</summary>
    Audiobooks = 3,

    /// <summary>E-books.</summary>
    Ebooks = 4,

    /// <summary>Collections (box sets).</summary>
    Collections = 5,

    /// <summary>Live TV.</summary>
    LiveTv = 6,
}
