using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Models.Requests.Admin;
using JellyShare.Domain.Models.Requests.Sync;
using JellyShare.UnitTests.Fakes;
using Xunit;

namespace JellyShare.UnitTests.Admin;

public sealed class AdminHandlerTests : IDisposable
{
    private readonly AdminHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ---- status ----

    [Fact]
    public async Task Status_ListsSharesPeersInPriorityOrderRootsAndSyncState()
    {
        _h.Config.Settings.Shares.Add(new PeerShare { Id = Guid.NewGuid(), Name = "bob", TokenHash = "h", Enabled = true, LibraryIds = [FakeLibraries.MoviesId], MaxStreams = 2 });
        _h.Config.Settings.Peers.Add(new RemotePeer { Id = Guid.NewGuid(), Name = "second", BaseUrl = "http://b", Token = "t", Priority = 1 });
        _h.Config.Settings.Peers.Add(new RemotePeer { Id = Guid.NewGuid(), Name = "first", BaseUrl = "http://a", Token = "t", Priority = 0 });
        _h.Libraries.Libraries[0] = _h.Libraries.Libraries[0] with { Locations = [_h.Config.Settings.MoviesPlaceholderPath] };
        _h.Sync.IsRunning = true;
        _h.Sync.LastError = "boom";

        var result = await _h.Send(new GetStatusQuery());

        Assert.True(result.IsSuccess);
        var status = result.Value;
        Assert.Equal(ProtocolInfo.Version, status.ProtocolVersion);
        Assert.Equal("Alice", status.ServerName);
        Assert.Equal("bob", Assert.Single(status.Shares).Name);
        Assert.Equal(["first", "second"], status.Peers.Select(p => p.Name));
        Assert.True(status.Movies.Attached);
        Assert.Equal(FakeLibraries.MoviesId, status.Movies.LibraryId);
        Assert.False(status.Shows.Attached);
        Assert.True(status.SyncRunning);
        Assert.Equal("boom", status.SyncError);
        Assert.Equal(3, status.Libraries.Count);
    }

    // ---- start sync ----

    [Fact]
    public async Task StartSync_StartsOne()
    {
        var result = await _h.Send(new StartSyncCommand());

        Assert.True(result.Value.Started);
        Assert.False(result.Value.AlreadyRunning);
        Assert.Equal(1, _h.Sync.BackgroundStarts);
    }

    [Fact]
    public async Task StartSync_WhenRunning_ReportsIt()
    {
        _h.Sync.IsRunning = true;

        var result = await _h.Send(new StartSyncCommand());

        Assert.False(result.Value.Started);
        Assert.True(result.Value.AlreadyRunning);
    }

    // ---- attach ----

    [Fact]
    public async Task Attach_AddsTheRootWithItsMarker()
    {
        var result = await _h.Send(new AttachLibraryCommand(FakeLibraries.MoviesId, "movies"));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Attached);
        Assert.Equal(("Movies", _h.Config.Settings.MoviesPlaceholderPath), Assert.Single(_h.Libraries.Added));
        Assert.True(File.Exists(Path.Combine(_h.Config.Settings.MoviesPlaceholderPath, ".jellyshare")));
    }

    [Fact]
    public async Task Attach_UnknownType_FailsValidation()
    {
        var result = await _h.Send(new AttachLibraryCommand(FakeLibraries.MoviesId, "Music"));

        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("Type must be Movies or Shows.", result.Error.Description);
    }

    [Fact]
    public async Task Attach_RootNotConfigured_FailsValidation()
    {
        _h.Config.Settings.ShowsPlaceholderPath = " ";

        var result = await _h.Send(new AttachLibraryCommand(FakeLibraries.ShowsId, "Shows"));

        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("The placeholder folder is not configured.", result.Error.Description);
    }

    [Fact]
    public async Task Attach_UnknownLibrary_IsNotFound()
    {
        var result = await _h.Send(new AttachLibraryCommand(Guid.NewGuid(), "Movies"));

        Assert.Equal(ErrorType.NotFound, result.Error.Type);
        Assert.Equal("Library not found.", result.Error.Description);
    }

    [Fact]
    public async Task Attach_WrongLibraryType_FailsValidation()
    {
        var result = await _h.Send(new AttachLibraryCommand(FakeLibraries.MusicId, "Movies"));

        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("'Music' is not a Movies library.", result.Error.Description);
    }

    [Fact]
    public async Task Attach_AlreadyInThatLibrary_SucceedsWithoutAddingAgain()
    {
        _h.Libraries.Libraries[0] = _h.Libraries.Libraries[0] with { Locations = [_h.Config.Settings.MoviesPlaceholderPath + "/"] };

        var result = await _h.Send(new AttachLibraryCommand(FakeLibraries.MoviesId, "Movies"));

        Assert.True(result.Value.Attached);
        Assert.Empty(_h.Libraries.Added);
    }

    [Fact]
    public async Task Attach_InAnotherLibrary_IsAConflict()
    {
        _h.Libraries.Libraries[2] = _h.Libraries.Libraries[2] with { Locations = [_h.Config.Settings.MoviesPlaceholderPath] };

        var result = await _h.Send(new AttachLibraryCommand(FakeLibraries.MoviesId, "Movies"));

        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Equal("This folder is already part of the library 'Music'. Remove it there first.", result.Error.Description);
    }

    [Fact]
    public async Task Attach_ForeignFolder_IsAConflict_AndNothingIsWritten()
    {
        var root = _h.Config.Settings.MoviesPlaceholderPath;
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "someone-else.txt"), "x");

        var result = await _h.Send(new AttachLibraryCommand(FakeLibraries.MoviesId, "Movies"));

        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Contains("no .jellyshare marker", result.Error.Description, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, ".jellyshare")));
    }

    [Fact]
    public async Task Attach_HostFailure_IsUnexpected_WithTheReason()
    {
        _h.Libraries.AddThrows = new InvalidOperationException("library is busy");

        var result = await _h.Send(new AttachLibraryCommand(FakeLibraries.MoviesId, "Movies"));

        Assert.Equal(ErrorType.Unexpected, result.Error.Type);
        Assert.Equal("Could not add the placeholder folder to 'Movies': library is busy", result.Error.Description);
    }
}
