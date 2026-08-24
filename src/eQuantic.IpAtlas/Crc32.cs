namespace eQuantic.IpAtlas;

/// <summary>
/// CRC-32 (IEEE 802.3, the polynomial zip and gzip use) over dataset bytes.
/// Datasets travel over networks and sit on disks for months; a checksum is
/// what turns silent bitrot into a loud failure at load time. Slice-by-8 so
/// checking a 20 MB dataset costs milliseconds, and standard so the value can
/// be verified with any external tool.
/// </summary>
public static class Crc32
{
    private const uint Polynomial = 0xEDB88320u;
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[8 * 256];
        for (var i = 0u; i < 256u; i++)
        {
            var entry = i;
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1) != 0 ? (entry >> 1) ^ Polynomial : entry >> 1;
            }

            table[i] = entry;
        }

        // Slice-by-8: each further slice is the previous one advanced by a byte.
        for (var i = 0; i < 256; i++)
        {
            for (var slice = 1; slice < 8; slice++)
            {
                var previous = table[((slice - 1) * 256) + i];
                table[(slice * 256) + i] = (previous >> 8) ^ table[previous & 0xFF];
            }
        }

        return table;
    }

    /// <summary>The CRC-32 of a byte span.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Finish(Update(Begin(), data));

    /// <summary>The starting state for a checksum built up over several writes.</summary>
    public static uint Begin() => 0xFFFFFFFFu;

    /// <summary>Turns a running state into the checksum to store.</summary>
    public static uint Finish(uint state) => ~state;

    /// <summary>Folds more bytes into a running checksum.</summary>
    public static uint Update(uint state, ReadOnlySpan<byte> data)
    {
        var crc = state;
        var table = Table;

        while (data.Length >= 8)
        {
            crc ^= (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
            crc = table[(7 * 256) + (crc & 0xFF)]
                ^ table[(6 * 256) + ((crc >> 8) & 0xFF)]
                ^ table[(5 * 256) + ((crc >> 16) & 0xFF)]
                ^ table[(4 * 256) + (crc >> 24)]
                ^ table[(3 * 256) + data[4]]
                ^ table[(2 * 256) + data[5]]
                ^ table[(1 * 256) + data[6]]
                ^ table[data[7]];
            data = data[8..];
        }

        foreach (var b in data)
        {
            crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }
}
