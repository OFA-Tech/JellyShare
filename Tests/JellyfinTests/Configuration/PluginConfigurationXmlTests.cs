using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Jellyfin.Configuration;

namespace JellyShare.JellyfinTests.Configuration;

/// <summary>The XML Jellyfin stores for the plugin must keep its shape so existing configurations load.</summary>
public sealed class PluginConfigurationXmlTests
{
    // Same shape as a configuration written by the plugin before the Domain split (made-up ids, hash and token).
    private const string Sample = """
        <?xml version="1.0" encoding="utf-16"?>
        <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <Shares>
            <PeerShare>
              <Id>11111111-2222-3333-4444-555555555555</Id>
              <Name>bob</Name>
              <TokenHash>0000000000000000000000000000000000000000000000000000000000000000</TokenHash>
              <Enabled>true</Enabled>
              <LibraryIds>
                <guid>aaaaaaaa-0000-0000-0000-000000000001</guid>
                <guid>aaaaaaaa-0000-0000-0000-000000000002</guid>
              </LibraryIds>
              <MaxStreams>2</MaxStreams>
              <MaxBitrate xsi:nil="true" />
            </PeerShare>
            <PeerShare>
              <Id>11111111-2222-3333-4444-666666666666</Id>
              <Name>carol</Name>
              <TokenHash>1111111111111111111111111111111111111111111111111111111111111111</TokenHash>
              <Enabled>false</Enabled>
              <LibraryIds />
              <MaxStreams>5</MaxStreams>
              <MaxBitrate>8000000</MaxBitrate>
            </PeerShare>
          </Shares>
          <Peers>
            <RemotePeer>
              <Id>99999999-8888-7777-6666-555555555555</Id>
              <Name>bob</Name>
              <BaseUrl>http://bob:8096/jellyfin</BaseUrl>
              <Token>js1_not-a-real-token</Token>
              <Enabled>true</Enabled>
              <Priority>0</Priority>
              <Kind>Token</Kind>
              <Username />
              <AccountUserId />
              <MaxStreams>1</MaxStreams>
              <LimitAction>Refuse</LimitAction>
            </RemotePeer>
            <RemotePeer>
              <Id>99999999-8888-7777-6666-444444444444</Id>
              <Name>carol</Name>
              <BaseUrl>http://carol:8096</BaseUrl>
              <Token>not-a-real-access-token</Token>
              <Enabled>true</Enabled>
              <Priority>1</Priority>
              <Kind>Account</Kind>
              <Username>carol</Username>
              <AccountUserId>0123456789abcdef0123456789abcdef</AccountUserId>
              <MaxStreams>3</MaxStreams>
              <LimitAction>StopOldest</LimitAction>
            </RemotePeer>
          </Peers>
          <MoviesPlaceholderPath>/config/plugins/JellyShare.Jellyfin/placeholders/Movies</MoviesPlaceholderPath>
          <ShowsPlaceholderPath>/config/plugins/JellyShare.Jellyfin/placeholders/Shows</ShowsPlaceholderPath>
          <SyncIntervalHours>6</SyncIntervalHours>
          <PresenceSweepSeconds>60</PresenceSweepSeconds>
          <MaxConcurrentTranscodes>4</MaxConcurrentTranscodes>
          <RelayBaseOverride>http://127.0.0.1:8096</RelayBaseOverride>
        </PluginConfiguration>
        """;

    [Fact]
    public void ExistingXml_Loads_AndSerializesBackUnchanged()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        PluginConfiguration cfg;
        using (var reader = XmlReader.Create(new StringReader(Sample), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
        {
            cfg = (PluginConfiguration)serializer.Deserialize(reader)!;
        }

        cfg.Shares.Count.ShouldBe(2);
        cfg.Shares[0].Name.ShouldBe("bob");
        cfg.Shares[0].LibraryIds.Count.ShouldBe(2);
        cfg.Shares[0].MaxBitrate.ShouldBeNull();
        cfg.Shares[1].Enabled.ShouldBeFalse();
        cfg.Shares[1].MaxBitrate.ShouldBe(8_000_000);
        cfg.Peers[0].Token.ShouldBe("js1_not-a-real-token");
        var account = cfg.Peers[1];
        account.Kind.ShouldBe(PeerKind.Account);
        account.Username.ShouldBe("carol");
        account.AccountUserId.ShouldBe("0123456789abcdef0123456789abcdef");
        account.MaxStreams.ShouldBe(3);
        account.LimitAction.ShouldBe(StreamLimitAction.StopOldest);
        cfg.RelayBaseOverride.ShouldBe("http://127.0.0.1:8096");

        using var writer = new StringWriter();
        serializer.Serialize(writer, cfg);

        XNode.DeepEquals(XDocument.Parse(Sample), XDocument.Parse(writer.ToString())).ShouldBeTrue(writer.ToString());
    }

    [Fact]
    public void PeerXmlWithoutAccountElements_LoadsAsTokenPeerWithDefaults()
    {
        const string legacy = """
            <PluginConfiguration>
              <Peers>
                <RemotePeer>
                  <Id>99999999-8888-7777-6666-555555555555</Id>
                  <Name>bob</Name>
                  <BaseUrl>http://bob:8096</BaseUrl>
                  <Token>js1_not-a-real-token</Token>
                  <Enabled>true</Enabled>
                  <Priority>0</Priority>
                </RemotePeer>
              </Peers>
            </PluginConfiguration>
            """;

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = XmlReader.Create(new StringReader(legacy), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var peer = ((PluginConfiguration)serializer.Deserialize(reader)!).Peers.ShouldHaveSingleItem();

        peer.Kind.ShouldBe(PeerKind.Token);
        peer.MaxStreams.ShouldBe(1);
        peer.LimitAction.ShouldBe(StreamLimitAction.Refuse);
    }
}
