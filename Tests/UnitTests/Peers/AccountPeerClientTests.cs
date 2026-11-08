using System.Net;
using System.Text;
using System.Text.Json;
using JellyShare.Data.Peers.Clients;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Protocol;
using JellyShare.UnitTests.Fakes;

namespace JellyShare.UnitTests.Peers;

public sealed class AccountPeerClientTests
{
    private const string Token = "jf-SECRET-access-token";
    private const string Password = "hunter2-SECRET";

    private const string MoviesAndEpisodes = """
        {"Items":[
          {"Id":"m1","Type":"Movie","Name":"Big Buck Bunny","ProductionYear":2008,"ProviderIds":{"Tmdb":"10378","Imdb":"tt1254207"},
           "RunTimeTicks":5964000000,"Overview":"A rabbit.","LocationType":"FileSystem","DateCreated":"2026-10-31T22:15:45Z",
           "ImageTags":{"Primary":"t1","Logo":"t2"},"BackdropImageTags":["b1"],
           "MediaSources":[{"Protocol":"File","Container":"mkv","Bitrate":8000000,"MediaStreams":[
             {"Type":"Video","Codec":"h264","Width":1920,"Height":1080},
             {"Type":"Audio","Codec":"aac","Language":"eng","Channels":2,"DisplayTitle":"English - AAC - Stereo"},
             {"Type":"Subtitle","Codec":"subrip","Language":"por","DisplayTitle":"Portuguese - SUBRIP"}]}]},
          {"Id":"e1","Type":"Episode","Name":"Pioneer One S01E01","SeriesName":"Pioneer One","SeriesId":"s1",
           "ParentIndexNumber":1,"IndexNumber":2,"ProviderIds":{},"LocationType":"FileSystem","DateCreated":"2026-10-30T00:00:00Z",
           "ImageTags":{},"BackdropImageTags":[],
           "MediaSources":[{"Protocol":"File","Container":"mkv","Bitrate":1312543,"MediaStreams":[]}]}
        ],"TotalRecordCount":2,"StartIndex":0}
        """;

    private const string Series = """{"Items":[{"Id":"s1","Type":"Series","ProviderIds":{"Tvdb":"153021"}}],"TotalRecordCount":1}""";

    private static RemotePeer Peer(string baseUrl = "https://h/jellyfin")
        => new()
        {
            Id = Guid.Parse("0123456789abcdef0123456789abcdef"),
            Name = "bob",
            BaseUrl = baseUrl,
            Token = Token,
            Kind = PeerKind.Account,
            Username = "carol",
            AccountUserId = "u1",
        };

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Server(HttpRequestMessage r, string items = MoviesAndEpisodes, int bitrateLimit = 0)
    {
        var path = r.RequestUri!.AbsolutePath;
        var query = r.RequestUri.Query;
        if (path.EndsWith("/System/Info/Public", StringComparison.Ordinal))
        {
            return Json("""{"ServerName":"bob","Version":"12.4.0"}""");
        }

        if (path.EndsWith("/Users/AuthenticateByName", StringComparison.Ordinal))
        {
            return Json("{\"AccessToken\":\"" + Token + "\",\"ServerId\":\"x\",\"User\":{\"Id\":\"u1\"}}");
        }

        if (path.EndsWith("/Users/Me", StringComparison.Ordinal))
        {
            return Json("{\"Id\":\"u1\",\"Policy\":{\"RemoteClientBitrateLimit\":" + bitrateLimit + "}}");
        }

        if (path.EndsWith("/Items", StringComparison.Ordinal))
        {
            return Json(query.Contains("IncludeItemTypes=Series", StringComparison.Ordinal) ? Series : items);
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
    }

    private static (AccountPeerClient Client, FakeHandler Handler) Create(FakeHandler handler, TimeProvider? clock = null)
        => (new AccountPeerClient(() => new HttpClient(handler, false), clock), handler);

    private static List<HttpRequestMessage> ItemRequests(FakeHandler handler, string type)
        => handler.Requests.Where(r => r.RequestUri!.AbsolutePath.EndsWith("/Items", StringComparison.Ordinal)
            && r.RequestUri.Query.Contains("IncludeItemTypes=" + type, StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task Account_SignIn_PostsCredentialsInBodyAndReturnsToken()
    {
        string? sentBody = null;
        var (client, handler) = Create(new FakeHandler(r =>
        {
            sentBody ??= r.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return Server(r);
        }));
        var peerId = Guid.NewGuid();

        var result = await client.SignInAsync("https://h/jellyfin", "carol", Password, peerId, CancellationToken.None);

        result.Ok.ShouldBeTrue(result.Error);
        result.AccessToken.ShouldBe(Token);
        result.UserId.ShouldBe("u1");
        result.ServerName.ShouldBe("bob");
        result.Error.ShouldBeNull();

        var post = handler.Requests[0];
        post.Method.ShouldBe(HttpMethod.Post);
        post.RequestUri!.ToString().ShouldBe("https://h/jellyfin/Users/AuthenticateByName");
        foreach (var r in handler.Requests)
        {
            var url = r.RequestUri!.ToString();
            url.ShouldNotContain("carol", Case.Sensitive);
            url.ShouldNotContain(Password, Case.Sensitive);
            url.ShouldNotContain(Token, Case.Sensitive);
        }

        using var body = JsonDocument.Parse(sentBody.ShouldNotBeNull());
        body.RootElement.GetProperty("Username").GetString().ShouldBe("carol");
        body.RootElement.GetProperty("Pw").GetString().ShouldBe(Password);

        var auth = post.Headers.GetValues("Authorization").Single();
        auth.ShouldStartWith("MediaBrowser ", Case.Sensitive);
        auth.ShouldContain("DeviceId=\"jellyshare-" + peerId.ToString("N") + "\"", Case.Sensitive);
        auth.ShouldContain("Client=\"JellyShare\"", Case.Sensitive);
        auth.ShouldNotContain("Token=", Case.Sensitive);
    }

    [Fact]
    public async Task Account_SignIn_401_IsWrongPassword()
    {
        var (client, _) = Create(new FakeHandler(_ => Json("{\"error\":\"" + Password + "\"}", HttpStatusCode.Unauthorized)));

        var result = await client.SignInAsync("https://h/jellyfin", "carol", Password, Guid.NewGuid(), CancellationToken.None);

        result.Ok.ShouldBeFalse();
        result.AccessToken.ShouldBeNull();
        result.Error.ShouldBe("sign-in failed: wrong username or password");
        result.Error.ShouldStartWith(AccountPeerInfo.SignInFailedPrefix, Case.Sensitive);
    }

    [Fact]
    public async Task Account_SignIn_ConnectionError_DoesNotLeakPassword()
    {
        var (client, _) = Create(new FakeHandler(_ => throw new HttpRequestException("refused " + Password)));

        var result = await client.SignInAsync("https://h/jellyfin", "carol", Password, Guid.NewGuid(), CancellationToken.None);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldStartWith("connection failed", Case.Sensitive);
        result.Error.ShouldNotContain(Password, Case.Sensitive);
    }

    [Fact]
    public async Task Account_SignIn_Redirect_IsRefused()
    {
        var (client, handler) = Create(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://evil.example/collect") },
        }));

        var result = await client.SignInAsync("https://h/jellyfin", "carol", Password, Guid.NewGuid(), CancellationToken.None);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldStartWith("HTTP 302", Case.Sensitive);
        handler.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Account_SignIn_InvalidBaseUrl_IsError()
    {
        var (client, handler) = Create(new FakeHandler(r => Server(r)));

        var result = await client.SignInAsync("ftp://h", "carol", Password, Guid.NewGuid(), CancellationToken.None);

        result.Error.ShouldBe("invalid base url");
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Account_SignOut_PostsLogoutWithToken()
    {
        var (client, handler) = Create(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));

        await client.SignOutAsync("https://h/jellyfin", Token, Guid.NewGuid(), CancellationToken.None);

        var r = handler.Requests.Single();
        r.Method.ShouldBe(HttpMethod.Post);
        r.RequestUri!.ToString().ShouldBe("https://h/jellyfin/Sessions/Logout");
        r.Headers.GetValues("Authorization").Single().ShouldContain("Token=\"" + Token + "\"", Case.Sensitive);
    }

    [Fact]
    public async Task Account_FetchCatalog_IncludeItemTypesFollowAccept()
    {
        var (client, handler) = Create(new FakeHandler(r => Server(r)));
        var peer = Peer();
        peer.AcceptAllTypes = false;
        peer.AcceptTypes = [MediaType.Movies, MediaType.Ebooks];

        var result = await client.FetchCatalogAsync(peer, null, CancellationToken.None);

        result.Ok.ShouldBeTrue(result.Error);
        result.Catalog!.Items.Select(i => i.Id).ShouldBe(["m1"]);
        ItemRequests(handler, "Movie&").Count.ShouldBe(1);
        ItemRequests(handler, "Series").ShouldBeEmpty();
        ItemRequests(handler, "Movie,Episode").ShouldBeEmpty();
    }

    [Fact]
    public async Task Account_FetchCatalog_NothingSupportedAccepted_AsksForNoItems()
    {
        var (client, handler) = Create(new FakeHandler(r => Server(r)));
        var peer = Peer();
        peer.AcceptAllTypes = false;
        peer.AcceptTypes = [MediaType.Ebooks];

        var result = await client.FetchCatalogAsync(peer, null, CancellationToken.None);

        result.Ok.ShouldBeTrue(result.Error);
        result.Catalog!.Items.ShouldBeEmpty();
        handler.Requests.ShouldNotContain(r => r.RequestUri!.AbsolutePath.EndsWith("/Items", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Account_Catalog_MapsMoviesAndEpisodes()
    {
        var (client, handler) = Create(new FakeHandler(r => Server(r)));

        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);

        result.Ok.ShouldBeTrue(result.Error);
        var catalog = result.Catalog!;
        catalog.ServerName.ShouldBe("bob");
        catalog.ProtocolVersion.ShouldBe(ProtocolInfo.Version);
        catalog.Etag.ShouldMatch("^[0-9a-f]{64}$");
        catalog.Items.Count.ShouldBe(2);

        var movie = catalog.Items[0];
        movie.Id.ShouldBe("m1");
        movie.Type.ShouldBe("Movie");
        movie.Name.ShouldBe("Big Buck Bunny");
        movie.Year.ShouldBe(2008);
        movie.ProviderIds["Tmdb"].ShouldBe("10378");
        movie.RunTimeTicks.ShouldBe(5964000000);
        movie.Overview.ShouldBe("A rabbit.");
        movie.DateModified.ShouldBe(new DateTime(2026, 10, 31, 22, 15, 45, DateTimeKind.Utc));
        movie.ImageTypes.ShouldBe(["Primary", "Logo", "Backdrop"], ignoreOrder: true);
        movie.Media.Container.ShouldBe("mkv");
        movie.Media.BitRate.ShouldBe(8000000);
        movie.Media.VideoCodec.ShouldBe("h264");
        movie.Media.Width.ShouldBe(1920);
        movie.Media.Height.ShouldBe(1080);
        movie.Media.Audio.ShouldHaveSingleItem().ShouldBe(new AudioTrack("eng", "aac", 2, "English - AAC - Stereo"));
        movie.Media.Subtitles.ShouldHaveSingleItem().ShouldBe(new SubtitleTrack("por", "subrip", "Portuguese - SUBRIP"));
        movie.SeriesProviderIds.ShouldBeNull();

        var episode = catalog.Items[1];
        episode.Type.ShouldBe("Episode");
        episode.SeriesName.ShouldBe("Pioneer One");
        episode.SeriesId.ShouldBe("s1");
        episode.SeriesProviderIds.ShouldNotBeNull()["Tvdb"].ShouldBe("153021");
        episode.SeasonNumber.ShouldBe(1);
        episode.EpisodeNumber.ShouldBe(2);
        episode.ImageTypes.ShouldBeEmpty();
        episode.Media.Audio.ShouldBeEmpty();

        var itemsUrl = ItemRequests(handler, "Movie").Single().RequestUri!.ToString();
        itemsUrl.ShouldContain("userId=u1", Case.Sensitive);
        itemsUrl.ShouldContain("Recursive=true", Case.Sensitive);
        itemsUrl.ShouldContain("Fields=ProviderIds,Overview,MediaSources,DateCreated", Case.Sensitive);
        itemsUrl.ShouldContain("Limit=500", Case.Sensitive);
        ItemRequests(handler, "Series").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Account_SkipsNonFileAndVirtualItems()
    {
        const string items = """
            {"Items":[
              {"Id":"ok","Type":"Movie","Name":"Kept","MediaSources":[{"Protocol":"File"}]},
              {"Id":"http","Type":"Movie","Name":"Placeholder","MediaSources":[{"Protocol":"Http"}]},
              {"Id":"virtual","Type":"Episode","Name":"Missing","LocationType":"Virtual","MediaSources":[{"Protocol":"File"}]},
              {"Id":"none","Type":"Movie","Name":"No sources"},
              {"Id":"empty","Type":"Movie","Name":"Empty sources","MediaSources":[]},
              {"Id":"ebook","Type":"Book","Name":"Not a supported type","MediaSources":[{"Protocol":"File"}]}
            ],"TotalRecordCount":6}
            """;
        var (client, _) = Create(new FakeHandler(r => Server(r, items)));

        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);

        result.Ok.ShouldBeTrue(result.Error);
        result.Catalog!.Items.Select(i => i.Id).ShouldBe(["ok"]);
    }

    private static int StartIndex(HttpRequestMessage r)
        => int.Parse(r.RequestUri!.Query.Split('&').Single(p => p.StartsWith("StartIndex=", StringComparison.Ordinal))["StartIndex=".Length..], System.Globalization.CultureInfo.InvariantCulture);

    // A server that answers at most perPage items from StartIndex on, out of total.
    private static HttpResponseMessage Paged(HttpRequestMessage r, int total, int perPage)
    {
        if (!r.RequestUri!.AbsolutePath.EndsWith("/Items", StringComparison.Ordinal) || r.RequestUri.Query.Contains("=Series", StringComparison.Ordinal))
        {
            return Server(r);
        }

        var start = StartIndex(r);
        var items = Enumerable.Range(start, Math.Max(0, Math.Min(perPage, total - start)))
            .Select(i => "{\"Id\":\"x" + i + "\",\"Type\":\"Movie\",\"Name\":\"x\",\"MediaSources\":[{\"Protocol\":\"File\"}]}");
        return Json("{\"Items\":[" + string.Join(",", items) + "],\"TotalRecordCount\":" + total + "}");
    }

    [Fact]
    public async Task Account_PagesItems()
    {
        var (client, handler) = Create(new FakeHandler(r => Paged(r, 1200, 500)));

        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);

        result.Ok.ShouldBeTrue(result.Error);
        result.Catalog!.Items.Count.ShouldBe(1200);
        ItemRequests(handler, "Movie").Select(StartIndex).ShouldBe([0, 500, 1000]);
    }

    [Fact]
    public async Task Account_ShortPages_AdvanceByItemsReceived()
    {
        var (client, handler) = Create(new FakeHandler(r => Paged(r, 700, 300)));

        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);

        result.Ok.ShouldBeTrue(result.Error);
        result.Catalog!.Items.Select(i => i.Id).ShouldBe(Enumerable.Range(0, 700).Select(i => "x" + i));
        ItemRequests(handler, "Movie").Select(StartIndex).ShouldBe([0, 300, 600]);
    }

    [Fact]
    public async Task Account_EmptyPage_EndsPaging()
    {
        // Claims 1200 items but only ever has 300.
        var (client, handler) = Create(new FakeHandler(r => Paged(r, 300, 500) is var response && r.RequestUri!.Query.Contains("=Movie", StringComparison.Ordinal)
            ? Json((response.Content.ReadAsStringAsync().GetAwaiter().GetResult()).Replace("\"TotalRecordCount\":300", "\"TotalRecordCount\":1200", StringComparison.Ordinal))
            : response));

        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);

        result.Ok.ShouldBeTrue(result.Error);
        result.Catalog!.Items.Count.ShouldBe(300);
        ItemRequests(handler, "Movie").Select(StartIndex).ShouldBe([0, 300]);
    }

    [Fact]
    public async Task Account_EndlessPaging_IsErrorAfterPageCap()
    {
        // Every page holds one item and the count never ends.
        var (client, handler) = Create(new FakeHandler(r => Paged(r, int.MaxValue, 1)));

        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe("too many pages");
        ItemRequests(handler, "Movie").Count.ShouldBe(1000);
    }

    [Fact]
    public async Task Account_ServerIgnoringStartIndex_IsErrorAtTheSecondPage()
    {
        // Always answers the first page, whatever StartIndex says.
        var (client, handler) = Create(new FakeHandler(r => Paged(r, 1200, 500) is var response && r.RequestUri!.Query.Contains("=Movie", StringComparison.Ordinal)
            ? Paged(new HttpRequestMessage(HttpMethod.Get, "https://h/Items?IncludeItemTypes=Movie&StartIndex=0"), 1200, 500)
            : response));

        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe("server repeats pages");
        ItemRequests(handler, "Movie").Count.ShouldBe(2);
    }

    [Fact]
    public async Task Account_SameContent_IsNotModified()
    {
        var (client, _) = Create(new FakeHandler(r => Server(r)));

        var first = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);
        var second = await client.FetchCatalogAsync(Peer(), first.Catalog!.Etag, CancellationToken.None);

        second.NotModified.ShouldBeTrue();
        second.Catalog.ShouldBeNull();
        second.Error.ShouldBeNull();
    }

    [Fact]
    public async Task Account_ChangedContent_HasNewEtag()
    {
        var changed = MoviesAndEpisodes.Replace("\"Big Buck Bunny\"", "\"Big Buck Bunny (Director's Cut)\"", StringComparison.Ordinal);
        var body = MoviesAndEpisodes;
        var (client, _) = Create(new FakeHandler(r => Server(r, body)));

        var first = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);
        body = changed;
        var second = await client.FetchCatalogAsync(Peer(), first.Catalog!.Etag, CancellationToken.None);

        second.Ok.ShouldBeTrue(second.Error);
        second.NotModified.ShouldBeFalse();
        second.Catalog!.Etag.ShouldNotBe(first.Catalog.Etag);
        second.Catalog.Items[0].Name.ShouldBe("Big Buck Bunny (Director's Cut)");
    }

    [Fact]
    public async Task Account_401_IsSignInFailed()
    {
        var (client, _) = Create(new FakeHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/Items", StringComparison.Ordinal)
            ? Json("{}", HttpStatusCode.Unauthorized)
            : Server(r)));

        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe("sign-in failed: HTTP 401");
    }

    [Fact]
    public async Task Account_CatalogRedirect_IsError()
    {
        var (client, _) = Create(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://evil.example/collect") },
        }));

        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);

        result.Error.ShouldBe("HTTP 302 (redirects are not followed)");
    }

    [Fact]
    public async Task Account_TokenOnlyInHeader()
    {
        var (client, handler) = Create(new FakeHandler(r => Server(r)));
        var peer = Peer();
        var catalog = await client.FetchCatalogAsync(peer, null, CancellationToken.None);
        await using var image = await client.GetImageAsync(peer, "m1", "Primary", CancellationToken.None);
        using var stream = await client.OpenStreamAsync(peer, catalog.Catalog!.Items[0], null, CancellationToken.None);

        image.ShouldNotBeNull();
        handler.Requests.ShouldContain(r => r.RequestUri!.ToString() == "https://h/jellyfin/Items/m1/Images/Primary");
        handler.Requests.ShouldContain(r => r.RequestUri!.AbsolutePath == "/jellyfin/Videos/m1/stream");
        foreach (var r in handler.Requests)
        {
            r.RequestUri!.ToString().ShouldNotContain(Token, Case.Sensitive);
            var auth = r.Headers.GetValues("Authorization").Single();
            auth.ShouldStartWith("MediaBrowser ", Case.Sensitive);
            auth.ShouldContain("Token=\"" + Token + "\"", Case.Sensitive);
            auth.ShouldContain("DeviceId=\"jellyshare-0123456789abcdef0123456789abcdef\"", Case.Sensitive);
        }
    }

    [Fact]
    public async Task Account_Stream_StaticWithRange()
    {
        var (client, handler) = Create(new FakeHandler(r => Server(r)));

        using var response = await client.OpenStreamAsync(Peer(), TestItem(8000000), "bytes=100-", CancellationToken.None);

        var r = handler.Requests.Single(x => x.RequestUri!.AbsolutePath.Contains("/Videos/", StringComparison.Ordinal));
        r.RequestUri!.ToString().ShouldBe("https://h/jellyfin/Videos/m1/stream?static=true");
        r.Headers.Range!.ToString().ShouldBe("bytes=100-");
    }

    [Fact]
    public async Task Account_Stream_TranscodesAboveBitrateLimit()
    {
        var (client, handler) = Create(new FakeHandler(r => Server(r, bitrateLimit: 2000000)));

        using var response = await client.OpenStreamAsync(Peer(), TestItem(8000000), "bytes=100-", CancellationToken.None);

        var r = handler.Requests.Single(x => x.RequestUri!.AbsolutePath.Contains("/Videos/", StringComparison.Ordinal));
        r.RequestUri!.ToString().ShouldStartWith(
            "https://h/jellyfin/Videos/m1/stream?static=false&container=mkv&videoCodec=h264&audioCodec=aac"
            + "&videoBitRate=1808000&audioBitRate=192000&deviceId=jellyshare-0123456789abcdef0123456789abcdef&playSessionId=",
            Case.Sensitive);
        r.Headers.Range.ShouldBeNull();
    }

    [Fact]
    public async Task Account_Stream_EachTranscodeGetsItsOwnPlaySessionId()
    {
        var (client, handler) = Create(new FakeHandler(r => Server(r, bitrateLimit: 2000000)));

        (await client.OpenStreamAsync(Peer(), TestItem(8000000), null, CancellationToken.None)).Dispose();
        (await client.OpenStreamAsync(Peer(), TestItem(8000000), null, CancellationToken.None)).Dispose();

        var ids = handler.Requests
            .Where(x => x.RequestUri!.AbsolutePath.Contains("/Videos/", StringComparison.Ordinal))
            .Select(x => System.Text.RegularExpressions.Regex.Match(x.RequestUri!.Query, "playSessionId=([0-9a-f]{32})$").Groups[1].Value)
            .ToList();
        ids.Count.ShouldBe(2);
        ids.ShouldAllBe(i => i.Length == 32);
        ids[0].ShouldNotBe(ids[1]);
    }

    [Fact]
    public async Task Account_Stream_BelowBitrateLimit_IsStatic()
    {
        var (client, handler) = Create(new FakeHandler(r => Server(r, bitrateLimit: 20000000)));

        using var response = await client.OpenStreamAsync(Peer(), TestItem(8000000), null, CancellationToken.None);

        handler.Requests[^1].RequestUri!.Query.ShouldBe("?static=true");
    }

    [Fact]
    public async Task Account_Policy_IsCachedForAnHour()
    {
        var clock = new ManualTimeProvider();
        var (client, handler) = Create(new FakeHandler(r => Server(r, bitrateLimit: 2000000)), clock);
        int PolicyCalls() => handler.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/Users/Me", StringComparison.Ordinal));

        (await client.OpenStreamAsync(Peer(), TestItem(8000000), null, CancellationToken.None)).Dispose();
        clock.Advance(TimeSpan.FromMinutes(59));
        (await client.OpenStreamAsync(Peer(), TestItem(8000000), null, CancellationToken.None)).Dispose();
        PolicyCalls().ShouldBe(1);

        clock.Advance(TimeSpan.FromMinutes(2));
        (await client.OpenStreamAsync(Peer(), TestItem(8000000), null, CancellationToken.None)).Dispose();
        PolicyCalls().ShouldBe(2);
    }

    [Fact]
    public async Task Account_Policy_NewToken_DoesNotReuseOldLimit()
    {
        // The first user has a 2 Mbit/s limit, the user signed in afterwards has none.
        var (client, handler) = Create(new FakeHandler(r => Server(
            r,
            bitrateLimit: r.Headers.GetValues("Authorization").Single().Contains("Token=\"" + Token + "\"", StringComparison.Ordinal) ? 2000000 : 0)));
        var peer = Peer();

        (await client.OpenStreamAsync(peer, TestItem(8000000), null, CancellationToken.None)).Dispose();
        handler.Requests[^1].RequestUri!.Query.ShouldStartWith("?static=false", Case.Sensitive);

        peer.Token = "other-user-token";
        (await client.OpenStreamAsync(peer, TestItem(8000000), null, CancellationToken.None)).Dispose();
        handler.Requests[^1].RequestUri!.Query.ShouldBe("?static=true");
    }

    [Fact]
    public async Task Account_Ping_IsFailedResultWithoutRequest()
    {
        var (client, handler) = Create(new FakeHandler(r => Server(r)));

        var result = await client.PingAsync("https://h/jellyfin", CancellationToken.None);

        result.Ok.ShouldBeFalse();
        result.Failure.ShouldBe(PingFailure.NotJellyShare);
        result.Detail.ShouldNotBeNullOrWhiteSpace();
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Account_Stream_ConnectionError_DoesNotLeakToken()
    {
        var (client, _) = Create(new FakeHandler(_ => throw new HttpRequestException("refused " + Token)));

        var ex = await Should.ThrowAsync<HttpRequestException>(() => client.OpenStreamAsync(Peer(), TestItem(null), null, CancellationToken.None));

        ex.Message.ShouldNotContain(Token, Case.Sensitive);
    }

    private const string AudioItems = """
        {"Items":[
          {"Id":"a1","Type":"Audio","Name":"Song Two","ProductionYear":2001,"ProviderIds":{"MusicBrainzRecording":"rec-2"},
           "RunTimeTicks":200000000,"LocationType":"FileSystem","DateCreated":"2026-10-30T00:00:00Z",
           "Album":"Album One","AlbumId":"al1","AlbumArtist":"Artist A","AlbumArtists":[{"Name":"Artist A","Id":"ar1"},{"Name":"Artist Z","Id":"ar2"}],
           "Artists":["Artist A","Guest"],"IndexNumber":2,"ParentIndexNumber":1,"ParentId":"al1","AlbumPrimaryImageTag":"t9",
           "ImageTags":{},"BackdropImageTags":[],
           "MediaSources":[{"Protocol":"File","Container":"flac","Bitrate":99332,"MediaStreams":[
             {"Type":"Audio","Codec":"flac","Channels":1,"DisplayTitle":"FLAC - Mono"}]}]},
          {"Id":"b1","Type":"AudioBook","Name":"Chapter One","ProviderIds":{},"LocationType":"FileSystem","DateCreated":"2026-10-30T00:00:00Z",
           "Album":"Book Y","AlbumArtist":"Author X","AlbumArtists":[{"Name":"Author X","Id":"ar3"}],"Artists":["Author X"],
           "IndexNumber":1,"ParentIndexNumber":1,"ParentId":"f2","ParentPrimaryImageItemId":"f2","ImageTags":{},"BackdropImageTags":[],
           "MediaSources":[{"Protocol":"File","Container":"mp3","Bitrate":64000,"MediaStreams":[]}]},
          {"Id":"b2","Type":"AudioBook","Name":"Chapter Two","ProviderIds":{},"LocationType":"FileSystem","DateCreated":"2026-10-30T00:00:00Z",
           "Album":"Book Y","IndexNumber":2,"ParentId":"f2","ImageTags":{},"BackdropImageTags":[],
           "MediaSources":[{"Protocol":"File","Container":"mp3","Bitrate":64000,"MediaStreams":[]}]},
          {"Id":"b3","Type":"AudioBook","Name":"Loose","ProviderIds":{},"LocationType":"FileSystem","DateCreated":"2026-10-30T00:00:00Z",
           "ParentId":"lib","ImageTags":{},"BackdropImageTags":[],
           "MediaSources":[{"Protocol":"File","Container":"mp3","Bitrate":64000,"MediaStreams":[]}]}
        ],"TotalRecordCount":4,"StartIndex":0}
        """;

    private static HttpResponseMessage AudioServer(HttpRequestMessage r)
    {
        var path = r.RequestUri!.AbsolutePath;
        if (path.EndsWith("/Items/b1/Ancestors", StringComparison.Ordinal))
        {
            return Json("""
                [{"Id":"f2","Type":"Folder","Name":"Book Y"},{"Id":"f1","Type":"Folder","Name":"Author X"},
                 {"Id":"lib","Type":"CollectionFolder","Name":"Audiobooks"},{"Id":"root","Type":"UserRootFolder","Name":"Media Folders"}]
                """);
        }

        if (path.EndsWith("/Items/b3/Ancestors", StringComparison.Ordinal))
        {
            return Json("""[{"Id":"lib","Type":"CollectionFolder","Name":"Audiobooks"},{"Id":"root","Type":"UserRootFolder","Name":"Media Folders"}]""");
        }

        return Server(r, AudioItems);
    }

    [Fact]
    public async Task Account_Audio_MapsAlbumFields()
    {
        var (client, handler) = Create(new FakeHandler(AudioServer));

        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);

        result.Ok.ShouldBeTrue(result.Error);
        var items = result.Catalog!.Items;
        var track = items.Single(i => i.Id == "a1");
        track.Type.ShouldBe("Audio");
        track.Album.ShouldBe("Album One");
        track.AlbumArtist.ShouldBe("Artist A");
        track.Artists.ShouldBe(["Artist A", "Guest"]);
        track.AlbumId.ShouldBe("al1");
        track.TrackNumber.ShouldBe(2);
        track.DiscNumber.ShouldBe(1);
        track.SeasonNumber.ShouldBeNull();
        track.EpisodeNumber.ShouldBeNull();
        track.ProviderIds["MusicBrainzRecording"].ShouldBe("rec-2");
        track.RunTimeTicks.ShouldBe(200000000);
        track.Media.Container.ShouldBe("flac");
        track.Media.BitRate.ShouldBe(99332);
        track.Media.Audio.ShouldHaveSingleItem().Codec.ShouldBe("flac");
        track.ImageTypes.ShouldBe(["Primary"]);
        track.FolderPath.ShouldBeNull();

        var book = items.Single(i => i.Id == "b1");
        book.Type.ShouldBe("AudioBook");
        book.FolderPath.ShouldBe("Author X/Book Y");
        book.AlbumId.ShouldBe("f2");
        book.Album.ShouldBe("Book Y");
        book.AlbumArtist.ShouldBe("Author X");
        book.ImageTypes.ShouldBe(["Primary"]);
        book.TrackNumber.ShouldBe(1);
        items.Single(i => i.Id == "b2").FolderPath.ShouldBe("Author X/Book Y");

        // A book file directly in the library root has no folder (as on a JellyShare provider).
        var loose = items.Single(i => i.Id == "b3");
        loose.FolderPath.ShouldBeNull();
        loose.AlbumId.ShouldBeNull();
        loose.ImageTypes.ShouldBeEmpty();

        // One parent lookup per book folder, not per file; no absolute path is ever asked for.
        handler.Requests.Where(x => x.RequestUri!.AbsolutePath.EndsWith("/Ancestors", StringComparison.Ordinal))
            .Select(x => x.RequestUri!.PathAndQuery).ShouldBe(["/jellyfin/Items/b1/Ancestors?userId=u1", "/jellyfin/Items/b3/Ancestors?userId=u1"]);
        ItemRequests(handler, "Movie,Episode,Audio,AudioBook").ShouldHaveSingleItem().RequestUri!.Query.ShouldNotContain("Path", Case.Sensitive);
        foreach (var r in handler.Requests)
        {
            r.RequestUri!.ToString().ShouldNotContain(Token, Case.Sensitive);
        }
    }

    [Fact]
    public async Task Account_Audio_FailedBookFolderLookup_ListsTheBookLoose_AndWarnsWithoutToken()
    {
        var handler = new FakeHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/Items/b1/Ancestors", StringComparison.Ordinal)
            ? Json("{\"error\":\"" + Token + "\"}", HttpStatusCode.InternalServerError)
            : AudioServer(r));
        var logger = new LevelLogger<AccountPeerClient>();
        var client = new AccountPeerClient(() => new HttpClient(handler, false), logger: logger);

        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);

        result.Ok.ShouldBeTrue(result.Error);
        result.Catalog!.Items.Where(i => i.Type == "AudioBook").Select(i => i.FolderPath).ShouldBe([null, null, null]);
        result.Catalog.Items.ShouldContain(i => i.Id == "a1");
        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(Microsoft.Extensions.Logging.LogLevel.Warning);
        warning.Message.ShouldContain("HTTP 500", Case.Sensitive);
        warning.Message.ShouldNotContain(Token, Case.Sensitive);
    }

    [Fact]
    public async Task Account_Audio_BookFolderLookupRejected_FailsAsSignIn()
    {
        var (client, _) = Create(new FakeHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/Ancestors", StringComparison.Ordinal)
            ? Json("{}", HttpStatusCode.Unauthorized)
            : AudioServer(r)));

        var result = await client.FetchCatalogAsync(Peer(), null, CancellationToken.None);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldStartWith(AccountPeerInfo.SignInFailedPrefix);
    }

    [Fact]
    public async Task Account_Audio_StreamsStaticAudio_EvenOverBitrateLimit()
    {
        var (client, handler) = Create(new FakeHandler(r => Server(r, bitrateLimit: 64000)));
        var track = TestItems.Movie("x") with { Id = "a1", Type = "Audio", Media = new MediaInfo("flac", null, null, null, 1000000, [], []) };

        using var response = await client.OpenStreamAsync(Peer(), track, "bytes=10-", CancellationToken.None);

        var r = handler.Requests.ShouldHaveSingleItem();
        r.RequestUri!.ToString().ShouldBe("https://h/jellyfin/Audio/a1/stream?static=true");
        r.Headers.Range!.ToString().ShouldBe("bytes=10-");
    }

    private static CatalogItem TestItem(int? bitRate)
        => TestItems.Movie("Big Buck Bunny") with { Media = new MediaInfo("mkv", "h264", 1920, 1080, bitRate, [], []) };
}
