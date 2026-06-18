using GlDrive.Plex;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Covers the pure Plex response parsers + connection picker — the fiddly,
/// network-free surface of the Plex invite manager. Sample payloads mirror the
/// shapes plex.tv returns (resources v2 JSON, server library JSON, classic
/// shared_servers XML).
/// </summary>
public class PlexClientTests
{
    [Fact]
    public void ParseResources_extracts_owned_servers_and_connections()
    {
        const string json = """
        [
          {
            "name": "Home Server",
            "clientIdentifier": "abc123",
            "provides": "server",
            "owned": true,
            "connections": [
              { "uri": "https://10-0-0-5.hash.plex.direct:32400", "protocol": "https", "address": "10.0.0.5", "port": 32400, "local": true, "relay": false },
              { "uri": "https://1-2-3-4.hash.plex.direct:32400", "protocol": "https", "address": "1.2.3.4", "port": 32400, "local": false, "relay": false }
            ]
          },
          {
            "name": "A Friend's Server",
            "clientIdentifier": "xyz789",
            "provides": "server",
            "owned": false,
            "connections": []
          },
          {
            "name": "Some Player",
            "clientIdentifier": "p1",
            "provides": "player",
            "owned": true,
            "connections": []
          }
        ]
        """;

        var resources = PlexClient.ParseResources(json);

        Assert.Equal(3, resources.Count);
        var owned = resources.Where(r => r.Owned && r.IsServer).ToList();
        Assert.Single(owned);
        Assert.Equal("Home Server", owned[0].Name);
        Assert.Equal("abc123", owned[0].ClientIdentifier);

        // Picker prefers the remote https connection over the local one.
        Assert.Equal("https://1-2-3-4.hash.plex.direct:32400", PlexClient.PickConnectionUri(owned[0]));
    }

    [Fact]
    public void PickConnectionUri_falls_back_to_relay_when_only_option()
    {
        var server = new PlexResource
        {
            Connections =
            {
                new PlexConnection { Uri = "https://relay.plex.direct:443", Protocol = "https", Relay = true, Local = false },
            },
        };
        Assert.Equal("https://relay.plex.direct:443", PlexClient.PickConnectionUri(server));
    }

    [Fact]
    public void ParseLibraries_reads_sections()
    {
        const string json = """
        {
          "MediaContainer": {
            "size": 2,
            "Directory": [
              { "key": "1", "title": "Movies", "type": "movie" },
              { "key": "2", "title": "TV Shows", "type": "show" }
            ]
          }
        }
        """;

        var libs = PlexClient.ParseLibraries(json);

        Assert.Equal(2, libs.Count);
        Assert.Equal("1", libs[0].Key);
        Assert.Equal("Movies", libs[0].Title);
        Assert.Equal("show", libs[1].Type);
    }

    [Fact]
    public void ParseSharedUsers_reads_users_and_their_sections()
    {
        const string xml = """
        <MediaContainer>
          <SharedServer id="555" username="bob" email="bob@example.com" userID="9001" accepted="1" allowSync="1">
            <Section id="11" key="1" title="Movies" type="movie" shared="1"/>
            <Section id="12" key="2" title="TV Shows" type="show" shared="0"/>
          </SharedServer>
          <SharedServer id="556" username="alice" email="alice@example.com" userID="9002" accepted="0" allowSync="0">
            <Section id="13" key="1" title="Movies" type="movie" shared="1"/>
          </SharedServer>
        </MediaContainer>
        """;

        var users = PlexClient.ParseSharedUsers(xml);

        Assert.Equal(2, users.Count);
        var bob = users[0];
        Assert.Equal("555", bob.SharedServerId);
        Assert.Equal("bob", bob.Username);
        Assert.True(bob.Accepted);
        Assert.True(bob.AllowSync);
        Assert.Equal("Yes", bob.DownloadsDisplay);
        Assert.Equal("Active", bob.StatusDisplay);
        Assert.Equal("Movies", bob.LibrariesDisplay); // only the shared=1 section

        var alice = users[1];
        Assert.False(alice.Accepted);
        Assert.Equal("Pending", alice.StatusDisplay);
        Assert.Equal("No", alice.DownloadsDisplay);
    }

    [Fact]
    public void Parsers_are_resilient_to_garbage()
    {
        Assert.Empty(PlexClient.ParseResources("not json"));
        Assert.Empty(PlexClient.ParseLibraries("{}"));
        Assert.Empty(PlexClient.ParseSharedUsers(""));
        Assert.Empty(PlexClient.ParseSharedUsers("<broken"));
    }

    [Fact]
    public void BuildAuthUrl_includes_client_id_and_code()
    {
        using var client = new PlexClient("client-guid-123", "GlDrive", "3.9.2");
        var url = client.BuildAuthUrl("PINCODE");
        Assert.Contains("clientID=client-guid-123", url);
        Assert.Contains("code=PINCODE", url);
        Assert.StartsWith("https://app.plex.tv/auth", url);
    }
}
