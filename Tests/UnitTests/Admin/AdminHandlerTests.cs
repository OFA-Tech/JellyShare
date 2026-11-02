using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Models.Requests.Admin;
using JellyShare.Domain.Models.Requests.Sync;
using JellyShare.UnitTests.Fakes;

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

        result.IsSuccess.ShouldBeTrue();
        var status = result.Value;
        status.ProtocolVersion.ShouldBe(ProtocolInfo.Version);
        status.ServerName.ShouldBe("Alice");
        status.Shares.ShouldHaveSingleItem().Name.ShouldBe("bob");
        status.Peers.Select(p => p.Name).ShouldBe(["first", "second"]);
        status.Movies.Attached.ShouldBeTrue();
        status.Movies.LibraryId.ShouldBe(FakeLibraries.MoviesId);
        status.Shows.Attached.ShouldBeFalse();
        status.SyncRunning.ShouldBeTrue();
        status.SyncError.ShouldBe("boom");
        status.Libraries.Count.ShouldBe(3);
    }

    // ---- start sync ----

    [Fact]
    public async Task StartSync_StartsOne()
    {
        var result = await _h.Send(new StartSyncCommand());

        result.Value.Started.ShouldBeTrue();
        result.Value.AlreadyRunning.ShouldBeFalse();
        _h.Sync.BackgroundStarts.ShouldBe(1);
    }

    [Fact]
    public async Task StartSync_WhenRunning_ReportsIt()
    {
        _h.Sync.IsRunning = true;

        var result = await _h.Send(new StartSyncCommand());

        result.Value.Started.ShouldBeFalse();
        result.Value.AlreadyRunning.ShouldBeTrue();
    }

    // ---- attach ----

    [Fact]
    public async Task Attach_AddsTheRootWithItsMarker()
    {
        var result = await _h.Send(new AttachLibraryCommand(FakeLibraries.MoviesId, "movies"));

        result.IsSuccess.ShouldBeTrue();
        result.Value.Attached.ShouldBeTrue();
        _h.Libraries.Added.ShouldHaveSingleItem().ShouldBe(("Movies", _h.Config.Settings.MoviesPlaceholderPath));
        File.Exists(Path.Combine(_h.Config.Settings.MoviesPlaceholderPath, ".jellyshare")).ShouldBeTrue();
    }

    [Fact]
    public async Task Attach_UnknownType_FailsValidation()
    {
        var result = await _h.Send(new AttachLibraryCommand(FakeLibraries.MoviesId, "Music"));

        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Description.ShouldBe("Type must be Movies or Shows.");
    }

    [Fact]
    public async Task Attach_RootNotConfigured_FailsValidation()
    {
        _h.Config.Settings.ShowsPlaceholderPath = " ";

        var result = await _h.Send(new AttachLibraryCommand(FakeLibraries.ShowsId, "Shows"));

        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Description.ShouldBe("The placeholder folder is not configured.");
    }

    [Fact]
    public async Task Attach_UnknownLibrary_IsNotFound()
    {
        var result = await _h.Send(new AttachLibraryCommand(Guid.NewGuid(), "Movies"));

        result.Error.Type.ShouldBe(ErrorType.NotFound);
        result.Error.Description.ShouldBe("Library not found.");
    }

    [Fact]
    public async Task Attach_WrongLibraryType_FailsValidation()
    {
        var result = await _h.Send(new AttachLibraryCommand(FakeLibraries.MusicId, "Movies"));

        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Description.ShouldBe("'Music' is not a Movies library.");
    }

    [Fact]
    public async Task Attach_AlreadyInThatLibrary_SucceedsWithoutAddingAgain()
    {
        _h.Libraries.Libraries[0] = _h.Libraries.Libraries[0] with { Locations = [_h.Config.Settings.MoviesPlaceholderPath + "/"] };

        var result = await _h.Send(new AttachLibraryCommand(FakeLibraries.MoviesId, "Movies"));

        result.Value.Attached.ShouldBeTrue();
        _h.Libraries.Added.ShouldBeEmpty();
    }

    [Fact]
    public async Task Attach_InAnotherLibrary_IsAConflict()
    {
        _h.Libraries.Libraries[2] = _h.Libraries.Libraries[2] with { Locations = [_h.Config.Settings.MoviesPlaceholderPath] };

        var result = await _h.Send(new AttachLibraryCommand(FakeLibraries.MoviesId, "Movies"));

        result.Error.Type.ShouldBe(ErrorType.Conflict);
        result.Error.Description.ShouldBe("This folder is already part of the library 'Music'. Remove it there first.");
    }

    [Fact]
    public async Task Attach_ForeignFolder_IsAConflict_AndNothingIsWritten()
    {
        var root = _h.Config.Settings.MoviesPlaceholderPath;
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "someone-else.txt"), "x");

        var result = await _h.Send(new AttachLibraryCommand(FakeLibraries.MoviesId, "Movies"));

        result.Error.Type.ShouldBe(ErrorType.Conflict);
        result.Error.Description.ShouldContain("no .jellyshare marker", Case.Sensitive);
        File.Exists(Path.Combine(root, ".jellyshare")).ShouldBeFalse();
    }

    [Fact]
    public async Task Attach_HostFailure_IsUnexpected_WithTheReason()
    {
        _h.Libraries.AddThrows = new InvalidOperationException("library is busy");

        var result = await _h.Send(new AttachLibraryCommand(FakeLibraries.MoviesId, "Movies"));

        result.Error.Type.ShouldBe(ErrorType.Unexpected);
        result.Error.Description.ShouldBe("Could not add the placeholder folder to 'Movies': library is busy");
    }
}
