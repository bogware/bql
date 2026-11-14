namespace Bogware.Bql;

/// <summary>iplocation [prefix=x] field: adds City, Region, Country, lat and lon for an IP address field.</summary>
public sealed class IplocationStage : BqlStage
{
    public override string StageName => "iplocation";

    /// <summary>The field holding the address.</summary>
    public required string Field { get; init; }

    /// <summary>Put before each added field's name.</summary>
    public string Prefix { get; init; } = string.Empty;
}

internal sealed class LegacyIplocationOperator : BqlOperator
{
    private readonly IplocationStage _stage;
    private readonly IBqlGeoIpLookup _lookup;

    public LegacyIplocationOperator(IplocationStage stage, IBqlGeoIpLookup? lookup)
    {
        _stage = stage;
        _lookup = lookup
            ?? throw new BqlExecutionException("iplocation needs a GeoIP database, and this host hasn't given one");
    }

    public override string Name => "iplocation";

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken cancellationToken = default) =>
        Task.FromResult<IEnumerable<dynamic>>(((IEnumerable<object>)input).Select(row =>
        {
            var dict = (IDictionary<string, object?>)row;
            if (dict.TryGetValue(_stage.Field, out var value) && value?.ToString() is { } address && _lookup.Lookup(address) is { } where)
            {
                dict[$"{_stage.Prefix}City"] = where.City;
                dict[$"{_stage.Prefix}Region"] = where.Region;
                dict[$"{_stage.Prefix}Country"] = where.Country;
                dict[$"{_stage.Prefix}country_code"] = where.CountryCode;
                dict[$"{_stage.Prefix}lat"] = where.Latitude;
                dict[$"{_stage.Prefix}lon"] = where.Longitude;
            }

            return row;
        }));
}
