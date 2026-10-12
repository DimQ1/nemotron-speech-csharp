using System.Buffers.Binary;

namespace SpeechLib.AudioStorage.Ogg;

/// <summary>Ogg page header flags (RFC 3533 §6).</summary>
[Flags]
internal enum OggPageFlags : byte
{
    None = 0,
    Continued = 0x01,
    BeginOfStream = 0x02,
    EndOfStream = 0x04,
}

/// <summary>Ogg page CRC: polynomial 0x04C11DB7, not reflected, init 0, no final xor.</summary>
internal static class OggCrc
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i << 24;
            for (int k = 0; k < 8; k++)
                r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
            table[i] = r;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data, uint crc = 0)
    {
        foreach (var b in data)
            crc = (crc << 8) ^ Table[(byte)(crc >> 24) ^ b];
        return crc;
    }
}

/// <summary>A parsed page (header fields + where it sits in the file).</summary>
internal readonly record struct OggPage(
    long Offset,
    int Length,
    OggPageFlags Flags,
    long Granule,
    uint Serial,
    uint Sequence,
    byte[] Lacing,
    byte[] Body)
{
    public const int HeaderSize = 27;
    public const int CrcOffset = 22;
    public const int FlagsOffset = 5;

    /// <summary>Splits the body into packets. A trailing 255 lacing value means the last packet continues on the next page.</summary>
    public IEnumerable<(ArraySegment<byte> Packet, bool Complete)> Packets()
    {
        int pos = 0, start = 0;
        for (int i = 0; i < Lacing.Length; i++)
        {
            pos += Lacing[i];
            if (Lacing[i] < 255)
            {
                yield return (new ArraySegment<byte>(Body, start, pos - start), true);
                start = pos;
            }
        }
        if (start < pos)
            yield return (new ArraySegment<byte>(Body, start, pos - start), false);
    }

    /// <summary>Serializes a page with a correct CRC.</summary>
    public static byte[] Build(OggPageFlags flags, long granule, uint serial, uint sequence, ReadOnlySpan<byte> lacing, ReadOnlySpan<byte> body)
    {
        var page = new byte[HeaderSize + lacing.Length + body.Length];
        "OggS"u8.CopyTo(page);
        page[4] = 0;
        page[FlagsOffset] = (byte)flags;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), sequence);
        page[26] = (byte)lacing.Length;
        lacing.CopyTo(page.AsSpan(HeaderSize));
        body.CopyTo(page.AsSpan(HeaderSize + lacing.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(CrcOffset), OggCrc.Compute(page));
        return page;
    }
}
