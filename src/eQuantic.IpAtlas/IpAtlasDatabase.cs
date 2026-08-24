using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace eQuantic.IpAtlas;

/// <summary>
/// An immutable, fully in-memory .eqatlas dataset. Loading parses the file into
/// structure-of-arrays form so a lookup is one binary search over contiguous
/// range starts — tens of nanoseconds, no allocation, safe from any thread.
/// Swap datasets by loading a new instance and replacing the reference.
/// </summary>
public sealed class IpAtlasDatabase
{
    private readonly uint[] _v4Starts;
    private readonly uint[] _v4Ends;
    private readonly ushort[] _v4Countries;
    private readonly uint[] _v4Asns;

    private readonly UInt128[] _v6Starts;
    private readonly UInt128[] _v6Ends;
    private readonly ushort[] _v6Countries;
    private readonly uint[] _v6Asns;

    /// <summary>When the dataset was compiled.</summary>
    public DateTimeOffset BuiltAt { get; }

    /// <summary>What the compiler said it was built from.</summary>
    public string Source { get; }

    /// <summary>Loaded IPv4 ranges.</summary>
    public int V4RangeCount => _v4Starts.Length;

    /// <summary>Loaded IPv6 ranges.</summary>
    public int V6RangeCount => _v6Starts.Length;

    private IpAtlasDatabase(
        DateTimeOffset builtAt, string source,
        uint[] v4Starts, uint[] v4Ends, ushort[] v4Countries, uint[] v4Asns,
        UInt128[] v6Starts, UInt128[] v6Ends, ushort[] v6Countries, uint[] v6Asns)
    {
        BuiltAt = builtAt;
        Source = source;
        _v4Starts = v4Starts;
        _v4Ends = v4Ends;
        _v4Countries = v4Countries;
        _v4Asns = v4Asns;
        _v6Starts = v6Starts;
        _v6Ends = v6Ends;
        _v6Countries = v6Countries;
        _v6Asns = v6Asns;
    }

    /// <summary>Loads a dataset from a file.</summary>
    public static IpAtlasDatabase Open(string path)
    {
        using var stream = File.OpenRead(path);
        return Open(stream);
    }

    /// <summary>Loads a dataset from a stream.</summary>
    public static IpAtlasDatabase Open(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        if (reader.ReadUInt32() != AtlasFormat.Magic)
        {
            throw new InvalidDataException("Not an .eqatlas dataset.");
        }

        var version = reader.ReadUInt16();
        if (version != AtlasFormat.Version)
        {
            throw new InvalidDataException($"Unsupported .eqatlas version {version}.");
        }

        _ = reader.ReadUInt16(); // flags, reserved
        var builtAt = DateTimeOffset.FromUnixTimeSeconds(reader.ReadInt64());
        var source = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16()));
        var v4Count = reader.ReadInt32();
        var v6Count = reader.ReadInt32();

        var v4Starts = new uint[v4Count];
        var v4Ends = new uint[v4Count];
        var v4Countries = new ushort[v4Count];
        var v4Asns = new uint[v4Count];
        for (var i = 0; i < v4Count; i++)
        {
            v4Starts[i] = reader.ReadUInt32();
            v4Ends[i] = reader.ReadUInt32();
            v4Countries[i] = reader.ReadUInt16();
            v4Asns[i] = reader.ReadUInt32();
        }

        var v6Starts = new UInt128[v6Count];
        var v6Ends = new UInt128[v6Count];
        var v6Countries = new ushort[v6Count];
        var v6Asns = new uint[v6Count];
        Span<byte> wide = stackalloc byte[16];
        for (var i = 0; i < v6Count; i++)
        {
            reader.BaseStream.ReadExactly(wide);
            v6Starts[i] = BinaryPrimitives.ReadUInt128BigEndian(wide);
            reader.BaseStream.ReadExactly(wide);
            v6Ends[i] = BinaryPrimitives.ReadUInt128BigEndian(wide);
            v6Countries[i] = reader.ReadUInt16();
            v6Asns[i] = reader.ReadUInt32();
        }

        return new IpAtlasDatabase(
            builtAt, source, v4Starts, v4Ends, v4Countries, v4Asns, v6Starts, v6Ends, v6Countries, v6Asns);
    }

    /// <summary>Looks a textual address up; unparsable input answers unknown.</summary>
    public IpInfo Lookup(string ipAddress) =>
        IPAddress.TryParse(ipAddress, out var parsed) ? Lookup(parsed) : IpInfo.Unknown;

    /// <summary>Looks an address up: country and ASN when the dataset knows the range.</summary>
    public IpInfo Lookup(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> bytes = stackalloc byte[4];
            address.TryWriteBytes(bytes, out _);
            return LookupV4(BinaryPrimitives.ReadUInt32BigEndian(bytes));
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Span<byte> bytes = stackalloc byte[16];
            address.TryWriteBytes(bytes, out _);
            return LookupV6(BinaryPrimitives.ReadUInt128BigEndian(bytes));
        }

        return IpInfo.Unknown;
    }

    private IpInfo LookupV4(uint value)
    {
        var index = UpperBound(_v4Starts, value);
        return index >= 0 && value <= _v4Ends[index]
            ? new IpInfo(
                AtlasFormat.UnpackCountry(_v4Countries[index]),
                _v4Asns[index] == 0 ? null : _v4Asns[index])
            : IpInfo.Unknown;
    }

    private IpInfo LookupV6(UInt128 value)
    {
        var index = UpperBound(_v6Starts, value);
        return index >= 0 && value <= _v6Ends[index]
            ? new IpInfo(
                AtlasFormat.UnpackCountry(_v6Countries[index]),
                _v6Asns[index] == 0 ? null : _v6Asns[index])
            : IpInfo.Unknown;
    }

    /// <summary>Index of the last element &lt;= value, or -1 when value precedes them all.</summary>
    private static int UpperBound<T>(T[] sorted, T value) where T : IComparable<T>
    {
        var index = Array.BinarySearch(sorted, value);
        return index >= 0 ? index : ~index - 1;
    }
}
