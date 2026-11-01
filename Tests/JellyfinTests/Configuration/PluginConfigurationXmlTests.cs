using System.Xml.Linq;
using System.Xml.Serialization;
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
        using (var reader = new StringReader(Sample))
        {
            cfg = (PluginConfiguration)serializer.Deserialize(reader)!;
        }

        cfg.Shares.Count.ShouldBe(2);
        cfg.Shares[0].Name.ShouldBe("bob");
        cfg.Shares[0].LibraryIds.Count.ShouldBe(2);
        cfg.Shares[0].MaxBitrate.ShouldBeNull();
        cfg.Shares[1].Enabled.ShouldBeFalse();
        cfg.Shares[1].MaxBitrate.ShouldBe(8_000_000);
        cfg.Peers.ShouldHaveSingleItem().Token.ShouldBe("js1_not-a-real-token");
        cfg.RelayBaseOverride.ShouldBe("http://127.0.0.1:8096");

        using var writer = new StringWriter();
        serializer.Serialize(writer, cfg);

        XNode.DeepEquals(XDocument.Parse(Sample), XDocument.Parse(writer.ToString())).ShouldBeTrue(writer.ToString());
    }
}
