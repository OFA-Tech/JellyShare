using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Library;
using JellyShare.Domain.Interfaces.Sync;
using JellyShare.Domain.Models.Admin;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Sync;

namespace JellyShare.UnitTests.Fakes;

/// <summary>Like <c>PluginSettingsStore</c>: only what is passed to <c>Save</c> becomes the stored settings.</summary>
internal sealed class FakeSaver : ISettingsStore
{
    private readonly SnapshotSettings _target;

    public FakeSaver(SnapshotSettings target)
    {
        _target = target;
    }

    public int Saves { get; private set; }

    public JellyShareSettings? Saved { get; private set; }

    public void Save(JellyShareSettings settings)
    {
        Saves++;
        Saved = settings;
        _target.Store(settings);
    }
}

internal sealed class FakeLibraries : IAdminLibraries
{
    public static readonly Guid MoviesId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid ShowsId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid MusicId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public List<LibraryInfo> Libraries { get; } = new()
    {
        new(MoviesId, "Movies", "movies", ["/media/Movies"], false),
        new(ShowsId, "Shows", "tvshows", ["/media/Shows"], true),
        new(MusicId, "Music", "music", ["/media/Music"], false),
    };

    public List<(string Library, string Path)> Added { get; } = new();

    public Exception? AddThrows { get; set; }

    public IReadOnlyList<LibraryInfo> List() => Libraries.ToList();

    public Task AddPathAsync(string libraryName, string path, CancellationToken ct)
    {
        if (AddThrows is not null)
        {
            throw AddThrows;
        }

        Added.Add((libraryName, path));
        var i = Libraries.FindIndex(l => l.Name == libraryName);
        Libraries[i] = Libraries[i] with { Locations = Libraries[i].Locations.Append(path).ToList() };
        return Task.CompletedTask;
    }
}

internal sealed class FakeSync : ISyncRunner
{
    public SyncReport? LastReport { get; set; }

    public int Runs { get; private set; }

    public Func<SyncReport> Report { get; set; } = () => new SyncReport(3, 1, 1, 2, 0, []);

    public bool IsRunning { get; set; }

    public string? LastError { get; set; }

    public int BackgroundStarts { get; private set; }

    public bool? LastQueueIfRunning { get; private set; }

    public Task<SyncReport> RunAsync(CancellationToken ct)
    {
        Runs++;
        LastReport = Report();
        return Task.FromResult(LastReport);
    }

    public bool TryStartBackground(bool queueIfRunning = false)
    {
        LastQueueIfRunning = queueIfRunning;
        if (IsRunning)
        {
            return false;
        }

        BackgroundStarts++;
        IsRunning = true;
        return true;
    }
}
