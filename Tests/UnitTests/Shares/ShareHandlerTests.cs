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
        var result = await _h.Send(new CreateShareCommand("  bob  ", null, 3, 8_000_000));

        result.IsSuccess.ShouldBeTrue();
        var saved = _h.Saver.Saved!.Shares.ShouldHaveSingleItem();
        saved.Id.ShouldBe(result.Value.Id);
        saved.Name.ShouldBe("bob");
        saved.UseDefaultTypes.ShouldBe(true);
        saved.Types.ShouldBeEmpty();
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
    public async Task CreateShare_DefaultsToUseDefault()
    {
        await _h.Send(new CreateShareCommand("bob", null, 2, null, null, [MediaType.Music]));

        var saved = _h.Saver.Saved!.Shares.ShouldHaveSingleItem();
        saved.UseDefaultTypes.ShouldBe(true);
        saved.Types.ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateShare_Custom_StoresTheTypes()
    {
        var result = await _h.Send(new CreateShareCommand("bob", null, 2, null, false, [MediaType.Shows, MediaType.Music]));

        result.IsSuccess.ShouldBeTrue();
        var saved = _h.Saver.Saved!.Shares.ShouldHaveSingleItem();
        saved.UseDefaultTypes.ShouldBe(false);
        saved.Types.ShouldBe([MediaType.Shows, MediaType.Music]);
    }

    [Theory]
    [InlineData(new MediaType[0], "Pick at least one media type to share.")]
    [InlineData(new[] { MediaType.Movies, MediaType.Movies }, "The media types are not valid. Pick each type at most once.")]
    [InlineData(new[] { (MediaType)99 }, "The media types are not valid. Pick each type at most once.")]
    [InlineData(new[] { MediaType.Ebooks, MediaType.LiveTv }, "Pick at least one supported media type.")]
    public async Task CreateShare_CustomWithBadTypes_FailsValidation(MediaType[] types, string message)
    {
        var result = await _h.Send(new CreateShareCommand("bob", null, 2, null, false, types));

        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Description.ShouldBe(message);
        _h.Saver.Saves.ShouldBe(0);
    }

    // ---- default sharing ----

    [Fact]
    public async Task UpdateDefaultSharing_All_StoresNull()
    {
        _h.Config.Settings.DefaultShareTypes = [MediaType.Movies];

        var result = await _h.Send(new UpdateDefaultSharingCommand(true, [MediaType.Music]));

        result.Value.All.ShouldBeTrue();
        result.Value.Types.ShouldBe(MediaTypes.All);
        _h.Saver.Saved!.DefaultShareTypes.ShouldBeNull();
    }

    [Fact]
    public async Task UpdateDefaultSharing_Custom_StoresTheTypes()
    {
        var result = await _h.Send(new UpdateDefaultSharingCommand(false, [MediaType.Shows, MediaType.Movies]));

        result.Value.All.ShouldBeFalse();
        result.Value.Types.ShouldBe([MediaType.Movies, MediaType.Shows]);
        _h.Saver.Saved!.DefaultShareTypes.ShouldBe([MediaType.Shows, MediaType.Movies]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task UpdateDefaultSharing_EmptyCustom_Rejected(bool? all)
    {
        var result = await _h.Send(new UpdateDefaultSharingCommand(all, []));

        result.Error.Type.ShouldBe(ErrorType.Validation);
        _h.Saver.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task UpdateDefaultSharing_OnlyUnsupportedTypes_Rejected()
    {
        var result = await _h.Send(new UpdateDefaultSharingCommand(false, [MediaType.Collections]));

        result.Error.Description.ShouldBe("Pick at least one supported media type.");
        _h.Saver.Saves.ShouldBe(0);
    }

    // ---- update ----

    [Fact]
    public async Task Update_ReplacesTheFields_KeepsTheTokenHash()
    {
        var share = Seed();

        var result = await _h.Send(new UpdateShareCommand(share.Id, "carol", null, 5, null, false));

        result.IsSuccess.ShouldBeTrue();
        result.Value.Name.ShouldBe("carol");
        result.Value.Enabled.ShouldBeFalse();
        var saved = _h.Saver.Saved!.Shares.ShouldHaveSingleItem();
        saved.TokenHash.ShouldBe("hash");
        saved.MaxStreams.ShouldBe(5);
        saved.ShouldNotBeSameAs(share);
    }

    [Fact]
    public async Task UpdateShare_Custom_StoresTypes_AndKeepsOtherFields()
    {
        var share = Seed();
        share.MaxBitrate = 4_000_000;

        var result = await _h.Send(new UpdateShareCommand(share.Id, "bob", null, 2, 4_000_000, null, false, [MediaType.Shows]));

        result.Value.UseDefaultTypes.ShouldBeFalse();
        result.Value.Types.ShouldBe([MediaType.Shows]);
        var saved = _h.Saver.Saved!.Shares.ShouldHaveSingleItem();
        saved.UseDefaultTypes.ShouldBe(false);
        saved.Types.ShouldBe([MediaType.Shows]);
        saved.Name.ShouldBe("bob");
        saved.MaxStreams.ShouldBe(2);
        saved.MaxBitrate.ShouldBe(4_000_000);
        saved.TokenHash.ShouldBe("hash");
        saved.Enabled.ShouldBeTrue();
        saved.LibraryIds.ShouldBe([FakeLibraries.MoviesId]);
    }

    [Fact]
    public async Task UpdateShare_TypesAlone_MakeItCustom_AndUseDefaultClearsThem()
    {
        var share = Seed();

        await _h.Send(new UpdateShareCommand(share.Id, "bob", null, 2, null, null, null, [MediaType.Movies]));
        _h.Saver.Saved!.Shares.Single().UseDefaultTypes.ShouldBe(false);

        await _h.Send(new UpdateShareCommand(share.Id, "bob", null, 2, null, null, true, null));
        var saved = _h.Saver.Saved.Shares.Single();
        saved.UseDefaultTypes.ShouldBe(true);
        saved.Types.ShouldBeEmpty();
    }

    [Fact]
    public async Task UpdateShare_Custom_WithoutTypes_FailsValidation()
    {
        var share = Seed();

        var result = await _h.Send(new UpdateShareCommand(share.Id, "bob", null, 2, null, null, false, []));

        result.Error.Type.ShouldBe(ErrorType.Validation);
        _h.Saver.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task UpdateShare_LegacyShare_ViewShowsDerivedTypes()
    {
        var share = Seed();

        var result = await _h.Send(new UpdateShareCommand(share.Id, "bob", null, 2, null, null));

        result.Value.UseDefaultTypes.ShouldBeFalse();
        result.Value.Types.ShouldBe([MediaType.Movies]);
        _h.Saver.Saved!.Shares.Single().UseDefaultTypes.ShouldBeNull();
    }

    [Fact]
    public async Task Update_KeepsTheMediaTypeChoice()
    {
        var share = Seed();
        share.UseDefaultTypes = false;
        share.Types = [JellyShare.Domain.Enums.MediaType.Shows];

        await _h.Send(new UpdateShareCommand(share.Id, "carol", null, 5, null, null));

        var saved = _h.Saver.Saved!.Shares.ShouldHaveSingleItem();
        saved.UseDefaultTypes.ShouldBe(false);
        saved.Types.ShouldBe([JellyShare.Domain.Enums.MediaType.Shows]);
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
