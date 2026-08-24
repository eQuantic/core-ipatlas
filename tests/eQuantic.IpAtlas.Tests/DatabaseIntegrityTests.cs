using System.Buffers.Binary;
using Shouldly;
using Xunit;

namespace eQuantic.IpAtlas.Tests;

/// <summary>
/// A dataset is an untrusted input. These are the files a reader must refuse
/// rather than crash on, hang on, or silently believe.
/// </summary>
public class DatabaseIntegrityTests
{
    private static byte[] Valid() => DatasetWriter.Build(
        v4: [new(0x02000000, 0x020FFFFF, "FR", 3215)],
        v6: [new(new UInt128(0x2A0104F800000000, 0), new UInt128(0x2A0104F8FFFFFFFF, ulong.MaxValue), "DE", 24940)]);

    [Fact]
    public void Opens_a_good_dataset()
    {
        var db = DatasetWriter.Open(Valid());

        db.FormatVersion.ShouldBe(2);
        db.V4RangeCount.ShouldBe(1);
        db.Lookup("2.0.0.1").CountryCode.ShouldBe("FR");
    }

    [Fact]
    public void Rejects_a_flipped_bit_instead_of_answering_from_it()
    {
        var file = Valid();
        file[^12] ^= 0xFF;

        var thrown = Should.Throw<InvalidDataException>(() => DatasetWriter.Open(file));

        thrown.Message.ShouldContain("checksum");
    }

    [Fact]
    public void Rejects_a_hostile_record_count_without_trying_to_allocate_it()
    {
        // A header claiming two billion records used to be believed, and the
        // process died reaching for the memory before reading a single record.
        var file = Valid();
        var countAt = FindSectionCountOffset(file);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(countAt), int.MaxValue);
        var resealed = DatasetWriter.Seal(file.AsSpan(0, file.Length - AtlasFormat.ChecksumSize).ToArray());

        Should.Throw<InvalidDataException>(() => DatasetWriter.Open(resealed));
    }

    [Fact]
    public void Rejects_a_negative_record_count()
    {
        var file = Valid();
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(FindSectionCountOffset(file)), -5);
        var resealed = DatasetWriter.Seal(file.AsSpan(0, file.Length - AtlasFormat.ChecksumSize).ToArray());

        Should.Throw<InvalidDataException>(() => DatasetWriter.Open(resealed));
    }

    [Fact]
    public void Rejects_a_truncated_file()
    {
        var file = Valid();

        Should.Throw<InvalidDataException>(() => DatasetWriter.Open(file[..(file.Length / 2)]));
    }

    [Fact]
    public void Rejects_an_empty_file() =>
        Should.Throw<InvalidDataException>(() => DatasetWriter.Open([]));

    [Fact]
    public void Rejects_something_that_is_not_a_dataset() =>
        Should.Throw<InvalidDataException>(() => DatasetWriter.Open("this is a text file, not a dataset"u8.ToArray()));

    [Fact]
    public void Rejects_a_layout_from_the_future()
    {
        var file = Valid();
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), 99);
        var resealed = DatasetWriter.Seal(file.AsSpan(0, file.Length - AtlasFormat.ChecksumSize).ToArray());

        var thrown = Should.Throw<InvalidDataException>(() => DatasetWriter.Open(resealed));

        thrown.Message.ShouldContain("upgrade");
    }

    [Fact]
    public void Rejects_ranges_that_are_not_in_order()
    {
        // Binary search over unsorted records does not fail, it answers wrongly.
        var file = DatasetWriter.BuildUnchecked(
        [
            new(0x0A000000, 0x0AFFFFFF, "FR"),
            new(0x02000000, 0x02FFFFFF, "GB"),
        ]);

        var thrown = Should.Throw<InvalidDataException>(() => DatasetWriter.Open(file));

        thrown.Message.ShouldContain("ascending");
    }

    [Fact]
    public void Rejects_a_range_that_ends_before_it_starts()
    {
        var file = DatasetWriter.BuildUnchecked([new(0x0AFFFFFF, 0x0A000000, "FR")]);

        Should.Throw<InvalidDataException>(() => DatasetWriter.Open(file));
    }

    [Fact]
    public void Still_reads_the_layout_shipped_in_version_one()
    {
        // Datasets built by 1.0.0 are in production. Refusing them would make an
        // upgrade a coordinated outage.
        var file = DatasetWriter.BuildV1(
            [new(0x02000000, 0x020FFFFF, "FR", 3215)],
            [new(new UInt128(0x2A0104F800000000, 0), new UInt128(0x2A0104F8FFFFFFFF, ulong.MaxValue), "DE", 24940)]);

        var db = DatasetWriter.Open(file);

        db.FormatVersion.ShouldBe(1);
        db.Lookup("2.0.0.1").CountryCode.ShouldBe("FR");
        db.Lookup("2.0.0.1").Asn.ShouldBe(3215u);
        db.Lookup("2a01:4f8::1").CountryCode.ShouldBe("DE");
        db.Lookup("2.0.0.1").Location.ShouldBeNull();
    }

    [Fact]
    public void TryOpen_reports_failure_instead_of_throwing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"broken-{Guid.NewGuid():N}.eqatlas");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8]);
        try
        {
            IpAtlasDatabase.TryOpen(path, out var database, out var error).ShouldBeFalse();
            database.ShouldBeNull();
            error.ShouldNotBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryOpen_reports_a_missing_file()
    {
        IpAtlasDatabase.TryOpen(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.eqatlas"),
            out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNull();
    }

    /// <summary>Byte offset of the first section entry's record count.</summary>
    private static int FindSectionCountOffset(byte[] file)
    {
        var sourceLength = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(16));
        return 18 + sourceLength + 1 + 1;
    }
}
