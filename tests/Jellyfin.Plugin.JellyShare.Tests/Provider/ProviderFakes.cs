using Jellyfin.Plugin.JellyShare.Configuration;
using Jellyfin.Plugin.JellyShare.Protocol;
using Jellyfin.Plugin.JellyShare.Provider;

namespace Jellyfin.Plugin.JellyShare.Tests.Provider;

internal sealed class FakeIdentity : IServerIdentity
{
    public string ServerName => "Alice";
}

internal sealed class FakeConfig : IConfigProvider
{
    public const string MoviesRoot = "/data/placeholders/Movies";
    public const string ShowsRoot = "/data/placeholders/Shows";

    public PluginConfiguration Current { get; } = new()
    {
        MoviesPlaceholderPath = MoviesRoot,
        ShowsPlaceholderPath = ShowsRoot,
    };
}

internal sealed class FakeSource : ILocalItemSource
{
    public List<(Guid Library, LocalItem Item)> Items { get; } = new();

    public Dictionary<(Guid, string), string> Images { get; } = new();

    public IEnumerable<LocalItem> GetItems(IReadOnlyCollection<Guid> libraryIds)
        => Items.Where(i => libraryIds.Contains(i.Library)).Select(i => i.Item);

    public string? GetImagePath(Guid itemId, string imageType)
        => Images.TryGetValue((itemId, imageType), out var p) ? p : null;
}
