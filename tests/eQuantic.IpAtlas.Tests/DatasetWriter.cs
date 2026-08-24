using System.Buffers.Binary;

namespace eQuantic.IpAtlas.Tests;

/// <summary>
/// Builds .eqatlas files the compiler would never produce, so the reader can be
/// tested against them: hostile counts, truncation, flipped bits, old layouts.
/// A reader is only as trustworthy as the malformed files it survives.
/// <para>
/// Valid files go through <see cref="AtlasWriter"/>, the same writer the
/// compiler and any consumer uses. This helper used to build those by hand too,
/// which meant the reader was being checked against this file's understanding of
/// the format rather than against the writer — two things that can drift apart
/// without a single test noticing.
/// </para>
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
        var writer = new AtlasWriter(source, builtAt ?? DateTimeOffset.UnixEpoch);

        foreach (var record in v4 ?? [])
        {
            writer.AddV4(record.Start, record.End, Describe(record.Country, record.Asn, record.Traits, record.Source, record.Location, places));
        }

        foreach (var record in v6 ?? [])
        {
            writer.AddV6(record.Start, record.End, Describe(record.Country, record.Asn, record.Traits, record.Source, record.Location, places));
        }

        var stream = new MemoryStream();
        writer.WriteTo(stream);
        return stream.ToArray();
    }

    /// <summary>Resolves the 1-based place id the fixtures use into the record the writer takes.</summary>
    private static AtlasRecord Describe(
        string? country, uint asn, NetworkTraits traits, LocationSource source, uint location,
        IReadOnlyList<Place>? places)
    {
        var record = new AtlasRecord(country, asn, traits, source);
        if (location == 0 || places is null || location > (uint)places.Count)
        {
            return record;
        }

        var place = places[(int)(location - 1)];
        return record with
        {
            Latitude = float.IsNaN(place.Latitude) ? null : place.Latitude,
            Longitude = float.IsNaN(place.Longitude) ? null : place.Longitude,
            Region = place.Region,
            City = place.City,
        };
    }

    /// <summary>
    /// Writes records exactly as given, without sorting them or checking that
    /// they are disjoint. <see cref="AtlasWriter"/> refuses to do this, which is
    /// the point of it — but the reader still has to survive a file that got
    /// this way, so the tests need a way to make one.
    /// </summary>
    internal static byte[] BuildUnchecked(IReadOnlyList<V4> v4, string source = "unchecked")
    {
        var records = new byte[v4.Count * AtlasFormat.V4RecordSize];
        for (var i = 0; i < v4.Count; i++)
        {
            AtlasFormat.WriteV4Record(
                records.AsSpan(i * AtlasFormat.V4RecordSize), v4[i].Start, v4[i].End,
                AtlasFormat.PackCountry(v4[i].Country), v4[i].Asn,
                AtlasFormat.PackTraits(v4[i].Traits, v4[i].Source), 0);
        }

        var offset = (long)AtlasFormat.HeaderSize(source, 4);
        var stream = new MemoryStream();
        AtlasFormat.WriteHeader(stream, DateTimeOffset.UnixEpoch, source,
        [
            new AtlasSection(AtlasSectionKind.V4Ranges, v4.Count, offset, records.Length),
            new AtlasSection(AtlasSectionKind.V6Ranges, 0, offset + records.Length, 0),
            new AtlasSection(AtlasSectionKind.Locations, 0, offset + records.Length, 0),
            new AtlasSection(AtlasSectionKind.Strings, 0, offset + records.Length, 0),
        ]);
        stream.Write(records);
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
}
