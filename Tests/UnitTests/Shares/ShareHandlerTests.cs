using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Requests.Shares;
using JellyShare.UnitTests.Fakes;
using Xunit;

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

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(_h.Saver.Saved!.Shares);
        Assert.Equal(result.Value.Id, saved.Id);
        Assert.Equal("bob", saved.Name);
        Assert.Equal([FakeLibraries.MoviesId], saved.LibraryIds);
        Assert.Equal(3, saved.MaxStreams);
        Assert.Equal(8_000_000, saved.MaxBitrate);
        Assert.True(saved.Enabled);
        Assert.NotEqual(result.Value.Token, saved.TokenHash);
        Assert.Same(saved, _h.Tokens.Verify(result.Value.Token, _h.Config.Current.Shares));
    }

    [Theory]
    [InlineData(" ", 2, null, "Enter a name for the invitation (at most 100 characters).")]
    [InlineData("bob", 0, null, "Max streams must be a number between 1 and 100.")]
    [InlineData("bob", 101, null, "Max streams must be a number between 1 and 100.")]
    [InlineData("bob", 2, 0, "Max bitrate must be between 0 and 2000 Mbps, or empty for no limit.")]
    public async Task Create_InvalidFields_FailValidation_AndSaveNothing(string name, int streams, int? bitrate, string message)
    {
        var result = await _h.Send(new CreateShareCommand(name, [FakeLibraries.MoviesId], streams, bitrate));

        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal(message, result.Error.Description);
        Assert.Equal(0, _h.Saver.Saves);
    }

    [Fact]
    public async Task Create_WithoutLibraries_FailsValidation()
    {
        var result = await _h.Send(new CreateShareCommand("bob", [], 2, null));

        Assert.Equal("Pick at least one library to share.", result.Error.Description);
    }

    [Fact]
    public async Task Create_WithAnUnknownLibrary_FailsValidation()
    {
        var result = await _h.Send(new CreateShareCommand("bob", [Guid.NewGuid()], 2, null));

        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("One of the selected libraries does not exist.", result.Error.Description);
    }

    // ---- update ----

    [Fact]
    public async Task Update_ReplacesTheFields_KeepsTheTokenHash()
    {
        var share = Seed();

        var result = await _h.Send(new UpdateShareCommand(share.Id, "carol", [FakeLibraries.ShowsId], 5, null, false));

        Assert.True(result.IsSuccess);
        Assert.Equal("carol", result.Value.Name);
        Assert.False(result.Value.Enabled);
        var saved = Assert.Single(_h.Saver.Saved!.Shares);
        Assert.Equal("hash", saved.TokenHash);
        Assert.Equal([FakeLibraries.ShowsId], saved.LibraryIds);
        Assert.NotSame(share, saved);
    }

    [Fact]
    public async Task Update_UnknownShare_IsNotFound_EvenWithInvalidFields()
    {
        var result = await _h.Send(new UpdateShareCommand(Guid.NewGuid(), " ", null, null, null, null));

        Assert.Equal(ErrorType.NotFound, result.Error.Type);
        Assert.Equal("Share not found.", result.Error.Description);
    }

    [Fact]
    public async Task Update_InvalidFields_FailValidation_AndSaveNothing()
    {
        var share = Seed();

        var result = await _h.Send(new UpdateShareCommand(share.Id, "bob", [FakeLibraries.MoviesId], 0, null, null));

        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("Max streams must be a number between 1 and 100.", result.Error.Description);
        Assert.Equal(0, _h.Saver.Saves);
    }

    // ---- delete ----

    [Fact]
    public async Task Delete_RemovesTheShare()
    {
        var share = Seed();

        var result = await _h.Send(new DeleteShareCommand(share.Id));

        Assert.True(result.IsSuccess);
        Assert.Empty(_h.Saver.Saved!.Shares);
    }

    [Fact]
    public async Task Delete_UnknownShare_IsNotFound()
    {
        var result = await _h.Send(new DeleteShareCommand(Guid.NewGuid()));

        Assert.Equal(ErrorType.NotFound, result.Error.Type);
        Assert.Equal("Share not found.", result.Error.Description);
        Assert.Equal(0, _h.Saver.Saves);
    }
}
