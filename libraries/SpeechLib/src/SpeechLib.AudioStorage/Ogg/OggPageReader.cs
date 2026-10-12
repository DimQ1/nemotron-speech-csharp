using System.Buffers.Binary;

namespace SpeechLib.AudioStorage.Ogg;

/// <summary>
/// Sequential page reader. Stops (without throwing) at the first page that is
/// truncated or fails its CRC — the normal state of a <c>.part</c> file after a crash.
/// </summary>
internal sealed class OggPageReader(Stream stream)
{
    private readonly byte[] _header = new byte[OggPage.HeaderSize];

    /// <summary>True once a damaged or truncated page was hit (as opposed to a clean end of file).</summary>
    public bool StoppedOnDamage { get; private set; }

    public bool TryReadPage(out OggPage page)
    {
        page = default;
        long offset = stream.Position;
        int got = stream.ReadAtLeast(_header, _header.Length, throwOnEndOfStream: false);
        if (got == 0)
            return false;
        if (got < _header.Length || !_header.AsSpan(0, 4).SequenceEqual("OggS"u8) || _header[4] != 0)
            return Damaged(offset);

        int segments = _header[26];
        var lacing = new byte[segments];
        if (stream.ReadAtLeast(lacing, segments, throwOnEndOfStream: false) < segments)
            return Damaged(offset);

        int bodyLength = 0;
        foreach (var l in lacing) bodyLength += l;
        var body = new byte[bodyLength];
        if (stream.ReadAtLeast(body, bodyLength, throwOnEndOfStream: false) < bodyLength)
            return Damaged(offset);

        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(OggPage.CrcOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(_header.AsSpan(OggPage.CrcOffset), 0);
        uint crc = OggCrc.Compute(_header);
        crc = OggCrc.Compute(lacing, crc);
        crc = OggCrc.Compute(body, crc);
        if (crc != storedCrc)
            return Damaged(offset);

        page = new OggPage(
            offset,
            OggPage.HeaderSize + segments + bodyLength,
            (OggPageFlags)_header[OggPage.FlagsOffset],
            BinaryPrimitives.ReadInt64LittleEndian(_header.AsSpan(6)),
            BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(14)),
            BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(18)),
            lacing,
            body);
        return true;
    }

    private bool Damaged(long offset)
    {
        StoppedOnDamage = true;
        stream.Position = offset;
        return false;
    }
}
