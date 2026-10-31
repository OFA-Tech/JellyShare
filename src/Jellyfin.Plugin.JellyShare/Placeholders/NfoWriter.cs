using System.Globalization;
using System.Text;
using System.Xml;
using Jellyfin.Plugin.JellyShare.Keys;
using Jellyfin.Plugin.JellyShare.Protocol;

namespace Jellyfin.Plugin.JellyShare.Placeholders;

/// <summary>Writes Kodi-style NFO files for placeholders. Output is deterministic.</summary>
public static class NfoWriter
{
    private static readonly string[] ItemKinds = ["tmdb", "imdb", "tvdb"];

    /// <summary>Writes the <c>movie.nfo</c> content.</summary>
    /// <param name="item">The movie.</param>
    /// <returns>The XML text.</returns>
    public static string Movie(CatalogItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Build("movie", w =>
        {
            Text(w, "title", item.Name);
            Text(w, "year", item.Year?.ToString(CultureInfo.InvariantCulture));
            Text(w, "plot", item.Overview);
            Text(w, "runtime", Minutes(item.RunTimeTicks));
            UniqueIds(w, OrderedIds(item.ProviderIds, ItemKinds));
            StreamDetails(w, item.Media);
        });
    }

    /// <summary>Writes the episode <c>.nfo</c> content.</summary>
    /// <param name="item">The episode.</param>
    /// <param name="showTitle">Overrides the show title (for example the owner's local series title).</param>
    /// <returns>The XML text.</returns>
    public static string Episode(CatalogItem item, string? showTitle = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Build("episodedetails", w =>
        {
            Text(w, "title", item.Name);
            Text(w, "showtitle", showTitle ?? item.SeriesName);
            Text(w, "season", item.SeasonNumber?.ToString(CultureInfo.InvariantCulture));
            Text(w, "episode", item.EpisodeNumber?.ToString(CultureInfo.InvariantCulture));
            Text(w, "year", item.Year?.ToString(CultureInfo.InvariantCulture));
            Text(w, "plot", item.Overview);
            Text(w, "runtime", Minutes(item.RunTimeTicks));
            UniqueIds(w, OrderedIds(item.ProviderIds, ItemKinds));
            StreamDetails(w, item.Media);
        });
    }

    /// <summary>Writes the <c>tvshow.nfo</c> content for the series of an episode.</summary>
    /// <param name="anyEpisode">Any episode of the series.</param>
    /// <param name="localSeries">The owner's local series; its title and provider ids are used when given.</param>
    /// <param name="seriesIds">
    /// The merged series ids of all sources (the set the folder tag is derived from); defaults to the episode's own.
    /// Ignored when a local series is given.
    /// </param>
    /// <returns>The XML text.</returns>
    public static string Series(
        CatalogItem anyEpisode,
        LocalSeries? localSeries = null,
        IReadOnlyDictionary<string, string>? seriesIds = null)
    {
        ArgumentNullException.ThrowIfNull(anyEpisode);
        var title = localSeries?.Name ?? anyEpisode.SeriesName;
        var ids = localSeries is not null
            ? OrderedIds(localSeries.ProviderIds, LocalSeries.SeriesKinds, includeOthers: true, validate: false)
            : OrderedIds(seriesIds ?? anyEpisode.SeriesProviderIds, LocalSeries.SeriesKinds);
        return Build("tvshow", w =>
        {
            Text(w, "title", title);
            UniqueIds(w, ids);
        });
    }

    private static string Build(string root, Action<XmlWriter> body)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
        };

        using var stream = new MemoryStream();
        using (var w = XmlWriter.Create(stream, settings))
        {
            w.WriteStartDocument(true);
            w.WriteStartElement(root);
            body(w);
            w.WriteEndElement();
            w.WriteEndDocument();
        }

        return new UTF8Encoding(false).GetString(stream.ToArray());
    }

    private static string? Minutes(long? ticks)
        => ticks is > 0
            ? Math.Round(ticks.Value / (double)TimeSpan.TicksPerMinute, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)
            : null;

    private static List<(string Kind, string Id)> OrderedIds(
        IEnumerable<KeyValuePair<string, string>>? ids,
        string[] kinds,
        bool includeOthers = false,
        bool validate = true)
    {
        var result = new List<(string, string)>();
        if (ids is null)
        {
            return result;
        }

        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in ids)
        {
            var id = validate ? MediaKey.NormalizeId(value) : (string.IsNullOrWhiteSpace(value) ? null : value.Trim());
            if (id is not null)
            {
                all.TryAdd(name.Trim(), id);
            }
        }

        foreach (var kind in kinds)
        {
            if (all.Remove(kind, out var id))
            {
                result.Add((kind, id));
            }
        }

        if (includeOthers)
        {
            foreach (var name in all.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                result.Add((name.ToLowerInvariant(), all[name]));
            }
        }

        return result;
    }

    private static void UniqueIds(XmlWriter w, List<(string Kind, string Id)> ids)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            w.WriteStartElement("uniqueid");
            w.WriteAttributeString("type", ids[i].Kind);
            if (i == 0)
            {
                w.WriteAttributeString("default", "true");
            }

            w.WriteString(Clean(ids[i].Id));
            w.WriteEndElement();
        }
    }

    private static void StreamDetails(XmlWriter w, MediaInfo? media)
    {
        if (media is null)
        {
            return;
        }

        var hasVideo = media.VideoCodec is not null || media.Width is not null || media.Height is not null;
        var audio = media.Audio ?? [];
        var subtitles = media.Subtitles ?? [];
        if (!hasVideo && audio.Length == 0 && subtitles.Length == 0)
        {
            return;
        }

        w.WriteStartElement("fileinfo");
        w.WriteStartElement("streamdetails");
        if (hasVideo)
        {
            w.WriteStartElement("video");
            Text(w, "codec", media.VideoCodec);
            Text(w, "width", media.Width?.ToString(CultureInfo.InvariantCulture));
            Text(w, "height", media.Height?.ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
        }

        foreach (var track in audio)
        {
            w.WriteStartElement("audio");
            Text(w, "codec", track.Codec);
            Text(w, "language", track.Language);
            Text(w, "channels", track.Channels?.ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
        }

        foreach (var track in subtitles)
        {
            w.WriteStartElement("subtitle");
            Text(w, "language", track.Language);
            w.WriteEndElement();
        }

        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void Text(XmlWriter w, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        w.WriteElementString(name, Clean(value));
    }

    private static string Clean(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                sb.Append(c).Append(value[i + 1]);
                i++;
            }
            else if (XmlConvert.IsXmlChar(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
