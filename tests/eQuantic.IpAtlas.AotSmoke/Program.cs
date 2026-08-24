using System.Buffers.Binary;
using eQuantic.IpAtlas;
using eQuantic.IpAtlas.Geo;

// Exercises every public entry point a consumer would reach for, so that
// publishing this natively is a real check rather than a compile of nothing.
var records = new byte[AtlasFormat.V4RecordSize];
AtlasFormat.WriteV4Record(
    records, 0x10000000, 0x1000FFFF, AtlasFormat.PackCountry("PT"), 1930,
    AtlasFormat.PackTraits(NetworkTraits.Hosting, LocationSource.CloudProvider), 0);

const string Source = "aot-smoke";
var offset = (long)AtlasFormat.HeaderSize(Source, 4);
var stream = new MemoryStream();
AtlasFormat.WriteHeader(stream, DateTimeOffset.UnixEpoch, Source,
[
    new AtlasSection(AtlasSectionKind.V4Ranges, 1, offset, records.Length),
    new AtlasSection(AtlasSectionKind.V6Ranges, 0, offset + records.Length, 0),
    new AtlasSection(AtlasSectionKind.Locations, 0, offset + records.Length, 0),
    new AtlasSection(AtlasSectionKind.Strings, 0, offset + records.Length, 0),
]);
stream.Write(records);

var body = stream.ToArray();
var file = new byte[body.Length + AtlasFormat.ChecksumSize];
body.CopyTo(file, 0);
BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(body.Length), Crc32.Compute(body));

var database = IpAtlasDatabase.Open(new MemoryStream(file));
var info = database.Lookup("16.0.0.1");
var scope = IpScopes.Classify(System.Net.IPAddress.Parse("10.0.0.1"));
var travel = Velocity.Assess(info, new IpInfo("JP", 1), TimeSpan.FromMinutes(10));

if (info.CountryCode != "PT" || !info.IsHosting || scope != IpScope.Private || travel.Plausible != false)
{
    Console.Error.WriteLine("AOT smoke test answered wrongly");
    return 1;
}

Console.WriteLine($"ok: {info.CountryCode} as{info.Asn} {info.Traits} / {scope} / {travel.Reason}");
return 0;
