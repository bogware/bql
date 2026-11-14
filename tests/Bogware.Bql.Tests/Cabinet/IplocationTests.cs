
namespace Bogware.Bql.Tests.Cabinet;

/// <summary>The GeoIP lookup is the host's, given in the engine's options.</summary>
public sealed class IplocationTests
{
    private sealed class FakeLookup : IBqlGeoIpLookup
    {
        public BqlGeoLocation? Lookup(string address) => address == "81.2.69.160"
            ? new BqlGeoLocation("London", "England", "United Kingdom", "GB", 51.5142, -0.0931)
            : null;
    }

    private static async Task<List<IDictionary<string, object?>>> RunAsync(string query, IBqlGeoIpLookup? lookup)
    {
        var storage = new InMemoryStorage();
        var a = new LogEvent { RawLine = "from 81.2.69.160" };
        a.SetField("clientip", "81.2.69.160");
        var b = new LogEvent { RawLine = "from 10.0.0.1" };
        b.SetField("clientip", "10.0.0.1");
        await storage.StoreBatchAsync([a, b]);
        var engine = new BqlQueryEngine(storage, new BqlEngineOptions { GeoIp = lookup });
        var result = await engine.ExecuteAsync(new BqlExecutionContext { Query = query });
        return result.Events.Select(e => (IDictionary<string, object?>)e).ToList();
    }

    [Fact]
    public async Task Iplocation_AddsWhereAnAddressIs()
    {
        {
            var lookup = new FakeLookup();
            var rows = await RunAsync("find * | iplocation clientip | stats count by Country, lat, lon", lookup);

            var row = rows.Should().ContainSingle("the private address has no location").Subject;
            row["Country"].Should().Be("United Kingdom");
            row["lat"].Should().Be(51.5142);

            var prefixed = await RunAsync("find * | iplocation prefix=src_ clientip | where isnotnull(src_City)", lookup);
            prefixed.Should().ContainSingle().Which["src_City"].Should().Be("London");
        }
    }

    [Fact]
    public async Task Iplocation_WithoutADatabase_SaysSo()
    {
        var act = () => RunAsync("find * | iplocation clientip", null);

        await act.Should().ThrowAsync<BqlExecutionException>().WithMessage("*GeoIP database*");
    }
}
