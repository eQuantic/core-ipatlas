using System.Buffers.Binary;
using System.Net;
using BenchmarkDotNet.Attributes;
using eQuantic.IpAtlas;
using eQuantic.IpAtlas.Geo;

namespace eQuantic.IpAtlas.Benchmarks;

/// <summary>
/// The numbers the README quotes. A dataset of half a million ranges is
/// generated in memory so the benchmark needs no downloaded file and measures
/// the same shape a world dataset has.
/// </summary>
[MemoryDiagnoser]
public class LookupBenchmarks
{
    private const int Ranges = 500_000;

    // Ranges start at 16.0.0.0 so every probe lands in publicly routable space.
    // Anchored at zero they would all fall in 0.0.0.0/8, which the scope check
    // answers before a lookup happens — a benchmark measuring the wrong thing.
    private const uint Base = 0x10000000;

    private byte[] _file = [];
    private IpAtlasDatabase _database = null!;
    private IPAddress[] _hits = [];
    private IPAddress[] _misses = [];
    private IPAddress _v6 = null!;
    private string _text = string.Empty;
    private int _cursor;

    [GlobalSetup]
    public void Setup()
    {
        _file = Synthesize();
        _database = IpAtlasDatabase.Open(new MemoryStream(_file));

        var random = new Random(20260824);
        _hits = new IPAddress[4096];
        _misses = new IPAddress[4096];
        for (var i = 0; i < _hits.Length; i++)
        {
            // Ranges are laid down every 64 addresses, 32 of them populated.
            var slot = (uint)random.Next(Ranges);
            _hits[i] = V4(Base + (slot * 64) + 1);
            _misses[i] = V4(Base + (slot * 64) + 40);
        }

        _v6 = IPAddress.Parse("2a01:4f8::1");
        _text = "16.4.0.1";
    }

    [Benchmark(Description = "Lookup, address in the dataset")]
    public IpInfo Hit() => _database.Lookup(_hits[_cursor++ & 4095]);

    [Benchmark(Description = "Lookup, address not in the dataset")]
    public IpInfo Miss() => _database.Lookup(_misses[_cursor++ & 4095]);

    [Benchmark(Description = "Lookup, IPv6")]
    public IpInfo V6Lookup() => _database.Lookup(_v6);

    [Benchmark(Description = "Lookup from text (includes parsing)")]
    public IpInfo FromText() => _database.Lookup(_text);

    [Benchmark(Description = "Classify scope only")]
    public IpScope Scope() => IpScopes.Classify(_hits[_cursor++ & 4095]);

    [Benchmark(Description = "Assess travel between two countries")]
    public TravelAssessment Travel() => Velocity.Assess("PT", "JP", TimeSpan.FromHours(2));

    [Benchmark(Description = "Open the whole dataset (checksum + parse)")]
    public IpAtlasDatabase Open() => IpAtlasDatabase.Open(new MemoryStream(_file));

    private static IPAddress V4(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }

    private static byte[] Synthesize()
    {
        var v4 = new byte[(long)Ranges * AtlasFormat.V4RecordSize];
        var random = new Random(7);
        for (var i = 0; i < Ranges; i++)
        {
            var start = Base + ((uint)i * 64);
            AtlasFormat.WriteV4Record(
                v4.AsSpan(i * AtlasFormat.V4RecordSize),
                start, start + 31,
                AtlasFormat.PackCountry(Codes[random.Next(Codes.Length)]),
                (uint)random.Next(1, 400_000),
                AtlasFormat.PackTraits(NetworkTraits.None, LocationSource.RegistryDelegation),
                0);
        }

        var v6 = new byte[AtlasFormat.V6RecordSize];
        AtlasFormat.WriteV6Record(
            v6,
            new UInt128(0x2A0104F800000000, 0),
            new UInt128(0x2A0104F8FFFFFFFF, ulong.MaxValue),
            AtlasFormat.PackCountry("DE"), 24940,
            AtlasFormat.PackTraits(NetworkTraits.None, LocationSource.RegistryDelegation), 0);

        const string Source = "benchmark";
        var offset = (long)AtlasFormat.HeaderSize(Source, 4);
        var sections = new List<AtlasSection>
        {
            new(AtlasSectionKind.V4Ranges, Ranges, offset, v4.Length),
            new(AtlasSectionKind.V6Ranges, 1, offset + v4.Length, v6.Length),
            new(AtlasSectionKind.Locations, 0, offset + v4.Length + v6.Length, 0),
            new(AtlasSectionKind.Strings, 0, offset + v4.Length + v6.Length, 0),
        };

        var stream = new MemoryStream();
        AtlasFormat.WriteHeader(stream, DateTimeOffset.UnixEpoch, Source, sections);
        stream.Write(v4);
        stream.Write(v6);

        var body = stream.ToArray();
        var file = new byte[body.Length + AtlasFormat.ChecksumSize];
        body.CopyTo(file, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(body.Length), Crc32.Compute(body));
        return file;
    }

    private static readonly string[] Codes =
        ["PT", "ES", "FR", "DE", "GB", "US", "BR", "JP", "AU", "ZA", "IN", "CA"];
}
