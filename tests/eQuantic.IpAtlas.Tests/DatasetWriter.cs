using System.Buffers.Binary;

namespace eQuantic.IpAtlas.Tests;

/// <summary>
/// Builds .eqatlas files by hand so the reader can be tested against inputs the
/// compiler would never produce: hostile counts, truncation, flipped bits, old
/// layouts. A reader is only as trustworthy as the malformed files it survives.
/// </summary>
internal static class DatasetWriter
{
    internal readonly record struct V4(uint Start, uint End, string? Country, uint Asn = 0,
        NetworkTraits Traits = NetworkTraits.None, LocationSource Source = LocationSource.None, uint Location = 0);

    internal readonly record struct V6(UInt128 Start, UInt128 End, string? Country, uint Asn = 0,
        NetworkTraits Traits = NetworkTraits.None, LocationSource Source = LocationSource.None, uint Location = 0);

    internal readonly record struct Place(float Latitude, float Longitude, string? Region, string? City);

    internal static byte[] Build(
        IReadOnlyList<V4>? v4 = null, IReadOnlyList<V6>? v6 = null, IReadOnlyList<Place>? places = null,
        string source = "test", DateTimeOffset? builtAt = null)
    {
        v4 ??= [];
        v6 ??= [];
        places ??= [];

        var (locationBytes, stringBytes) = Serialize(places);
        var offset = (long)AtlasFormat.HeaderSize(source, 4);
        var sections = new List<AtlasSection>();

        long Place(AtlasSectionKind kind, int count, long length)
        {
            sections.Add(new AtlasSection(kind, count, offset, length));
            return offset += length;
        }

        Place(AtlasSectionKind.V4Ranges, v4.Count, (long)v4.Count * AtlasFormat.V4RecordSize);
        Place(AtlasSectionKind.V6Ranges, v6.Count, (long)v6.Count * AtlasFormat.V6RecordSize);
        Place(AtlasSectionKind.Locations, places.Count, locationBytes.Length);
        Place(AtlasSectionKind.Strings, stringBytes.Length, stringBytes.Length);

        var stream = new MemoryStream();
        AtlasFormat.WriteHeader(stream, builtAt ?? DateTimeOffset.UnixEpoch, source, sections);

        var buffer = new byte[AtlasFormat.V6RecordSize];
        foreach (var record in v4)
        {
            AtlasFormat.WriteV4Record(
                buffer, record.Start, record.End, AtlasFormat.PackCountry(record.Country),
                record.Asn, AtlasFormat.PackTraits(record.Traits, record.Source), record.Location);
            stream.Write(buffer, 0, AtlasFormat.V4RecordSize);
        }

        foreach (var record in v6)
        {
            AtlasFormat.WriteV6Record(
                buffer, record.Start, record.End, AtlasFormat.PackCountry(record.Country),
                record.Asn, AtlasFormat.PackTraits(record.Traits, record.Source), record.Location);
            stream.Write(buffer, 0, AtlasFormat.V6RecordSize);
        }

        stream.Write(locationBytes, 0, locationBytes.Length);
        stream.Write(stringBytes, 0, stringBytes.Length);
        return Seal(stream.ToArray());
    }

    /// <summary>Appends the checksum a valid file ends with.</summary>
    internal static byte[] Seal(byte[] body)
    {
        var file = new byte[body.Length + AtlasFormat.ChecksumSize];
        body.CopyTo(file, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(body.Length), Crc32.Compute(body));
        return file;
    }

    /// <summary>Writes a version 1 file, the layout shipped before section tables existed.</summary>
    internal static byte[] BuildV1(IReadOnlyList<V4> v4, IReadOnlyList<V6> v6, string source = "v1")
    {
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        writer.Write(AtlasFormat.Magic);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write(DateTimeOffset.UnixEpoch.ToUnixTimeSeconds());
        var sourceBytes = System.Text.Encoding.UTF8.GetBytes(source);
        writer.Write((ushort)sourceBytes.Length);
        writer.Write(sourceBytes);
        writer.Write(v4.Count);
        writer.Write(v6.Count);

        foreach (var record in v4)
        {
            writer.Write(record.Start);
            writer.Write(record.End);
            writer.Write(AtlasFormat.PackCountry(record.Country));
            writer.Write(record.Asn);
        }

        var wide = new byte[16];
        foreach (var record in v6)
        {
            BinaryPrimitives.WriteUInt128BigEndian(wide, record.Start);
            writer.Write(wide);
            BinaryPrimitives.WriteUInt128BigEndian(wide, record.End);
            writer.Write(wide);
            writer.Write(AtlasFormat.PackCountry(record.Country));
            writer.Write(record.Asn);
        }

        writer.Flush();
        return stream.ToArray();
    }

    internal static IpAtlasDatabase Open(byte[] file) => IpAtlasDatabase.Open(new MemoryStream(file));

    private static (byte[] Locations, byte[] Strings) Serialize(IReadOnlyList<Place> places)
    {
        var blob = new List<byte>();
        var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);

        uint Intern(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return 0;
            }

            if (offsets.TryGetValue(value, out var existing))
            {
                return existing;
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            var offset = (uint)blob.Count + 1;
            blob.Add((byte)bytes.Length);
            blob.AddRange(bytes);
            offsets[value] = offset;
            return offset;
        }

        var locations = new byte[places.Count * AtlasFormat.LocationRecordSize];
        for (var i = 0; i < places.Count; i++)
        {
            AtlasFormat.WriteLocationRecord(
                locations.AsSpan(i * AtlasFormat.LocationRecordSize),
                places[i].Latitude, places[i].Longitude, Intern(places[i].Region), Intern(places[i].City));
        }

        return (locations, blob.ToArray());
    }
}
