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

    public TimeSpan GetItemsDelay { get; set; }

    public int GetItemsCalls => _calls;

    private int _calls;

    public IEnumerable<LocalItem> GetItems(IReadOnlyCollection<Guid> libraryIds)
    {
        Interlocked.Increment(ref _calls);
        if (GetItemsDelay > TimeSpan.Zero)
        {
            Thread.Sleep(GetItemsDelay);
        }

        return Items.Where(i => libraryIds.Contains(i.Library)).Select(i => i.Item).ToList();
    }

    public string? GetImagePath(Guid itemId, string imageType)
        => Images.TryGetValue((itemId, imageType), out var p) ? p : null;
}
