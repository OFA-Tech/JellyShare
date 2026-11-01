using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Requests.Shares;
using JellyShare.UnitTests.Fakes;

namespace JellyShare.UnitTests.Shares;

public sealed class ShareHandlerTests : IDisposable
{
    private readonly AdminHarness _h = new();

    public void Dispose() => _h.Dispose();

    private PeerShare Seed(string name = "bob")
    {
        var share = new PeerShare { Id = Guid.NewGuid(), Name = name, TokenHash = "hash", Enabled = true, LibraryIds = [FakeLibraries.MoviesId], MaxStreams = 2 };
        _h.Config.Settings.Shares.Add(share);
        return share;
    }

    // ---- create ----

    [Fact]
    public async Task Create_SavesTheShareWithTheTokenHash_AndReturnsTheTokenOnce()
    {
        var result = await _h.Send(new CreateShareCommand("  bob  ", [FakeLibraries.MoviesId, FakeLibraries.MoviesId], 3, 8_000_000));

        result.IsSuccess.ShouldBeTrue();
        var saved = _h.Saver.Saved!.Shares.ShouldHaveSingleItem();
        saved.Id.ShouldBe(result.Value.Id);
        saved.Name.ShouldBe("bob");
        saved.LibraryIds.ShouldBe([FakeLibraries.MoviesId]);
        saved.MaxStreams.ShouldBe(3);
        saved.MaxBitrate.ShouldBe(8_000_000);
        saved.Enabled.ShouldBeTrue();
        saved.TokenHash.ShouldNotBe(result.Value.Token);
        _h.Tokens.Verify(result.Value.Token, _h.Config.Current.Shares).ShouldBeSameAs(saved);
    }

    [Theory]
    [InlineData(" ", 2, null, "Enter a name for the invitation (at most 100 characters).")]
    [InlineData("bob", 0, null, "Max streams must be a number between 1 and 100.")]
    [InlineData("bob", 101, null, "Max streams must be a number between 1 and 100.")]
    [InlineData("bob", 2, 0, "Max bitrate must be between 0 and 2000 Mbps, or empty for no limit.")]
    public async Task Create_InvalidFields_FailValidation_AndSaveNothing(string name, int streams, int? bitrate, string message)
    {
        var result = await _h.Send(new CreateShareCommand(name, [FakeLibraries.MoviesId], streams, bitrate));

        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Description.ShouldBe(message);
        _h.Saver.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task Create_WithoutLibraries_FailsValidation()
    {
        var result = await _h.Send(new CreateShareCommand("bob", [], 2, null));

        result.Error.Description.ShouldBe("Pick at least one library to share.");
    }

    [Fact]
    public async Task Create_WithAnUnknownLibrary_FailsValidation()
    {
        var result = await _h.Send(new CreateShareCommand("bob", [Guid.NewGuid()], 2, null));

        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Description.ShouldBe("One of the selected libraries does not exist.");
    }

    // ---- update ----

    [Fact]
    public async Task Update_ReplacesTheFields_KeepsTheTokenHash()
    {
        var share = Seed();

        var result = await _h.Send(new UpdateShareCommand(share.Id, "carol", [FakeLibraries.ShowsId], 5, null, false));

        result.IsSuccess.ShouldBeTrue();
        result.Value.Name.ShouldBe("carol");
        result.Value.Enabled.ShouldBeFalse();
        var saved = _h.Saver.Saved!.Shares.ShouldHaveSingleItem();
        saved.TokenHash.ShouldBe("hash");
        saved.LibraryIds.ShouldBe([FakeLibraries.ShowsId]);
        saved.ShouldNotBeSameAs(share);
    }

    [Fact]
    public async Task Update_UnknownShare_IsNotFound_EvenWithInvalidFields()
    {
        var result = await _h.Send(new UpdateShareCommand(Guid.NewGuid(), " ", null, null, null, null));

        result.Error.Type.ShouldBe(ErrorType.NotFound);
        result.Error.Description.ShouldBe("Share not found.");
    }

    [Fact]
    public async Task Update_InvalidFields_FailValidation_AndSaveNothing()
    {
        var share = Seed();

        var result = await _h.Send(new UpdateShareCommand(share.Id, "bob", [FakeLibraries.MoviesId], 0, null, null));

        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Description.ShouldBe("Max streams must be a number between 1 and 100.");
        _h.Saver.Saves.ShouldBe(0);
    }

    // ---- delete ----

    [Fact]
    public async Task Delete_RemovesTheShare()
    {
        var share = Seed();

        var result = await _h.Send(new DeleteShareCommand(share.Id));

        result.IsSuccess.ShouldBeTrue();
        _h.Saver.Saved!.Shares.ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_UnknownShare_IsNotFound()
    {
        var result = await _h.Send(new DeleteShareCommand(Guid.NewGuid()));

        result.Error.Type.ShouldBe(ErrorType.NotFound);
        result.Error.Description.ShouldBe("Share not found.");
        _h.Saver.Saves.ShouldBe(0);
    }
}
