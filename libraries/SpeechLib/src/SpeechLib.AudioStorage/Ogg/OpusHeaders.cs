using System.Buffers.Binary;
using System.Text;

namespace SpeechLib.AudioStorage.Ogg;

/// <summary>OpusHead / OpusTags packets (RFC 7845 §5).</summary>
internal static class OpusHeaders
{
    /// <summary>Opus granule positions always count 48 kHz samples.</summary>
    public const int GranuleRate = 48000;

    public static byte[] BuildHead(int channels, int preSkip48k, int inputSampleRate)
    {
        var head = new byte[19];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1; // version
        head[9] = (byte)channels;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), (ushort)preSkip48k);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), (uint)inputSampleRate);
        BinaryPrimitives.WriteInt16LittleEndian(head.AsSpan(16), 0); // output gain
        head[18] = 0; // channel mapping family 0 (mono/stereo)
        return head;
    }

    public static byte[] BuildTags(string vendor, IEnumerable<string> comments)
    {
        var vendorBytes = Encoding.UTF8.GetBytes(vendor);
        var commentBytes = comments.Select(Encoding.UTF8.GetBytes).ToList();
        var tags = new byte[8 + 4 + vendorBytes.Length + 4 + commentBytes.Sum(c => 4 + c.Length)];
        "OpusTags"u8.CopyTo(tags);
        int pos = 8;
        BinaryPrimitives.WriteUInt32LittleEndian(tags.AsSpan(pos), (uint)vendorBytes.Length); pos += 4;
        vendorBytes.CopyTo(tags, pos); pos += vendorBytes.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(tags.AsSpan(pos), (uint)commentBytes.Count); pos += 4;
        foreach (var c in commentBytes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(tags.AsSpan(pos), (uint)c.Length); pos += 4;
            c.CopyTo(tags, pos); pos += c.Length;
        }
        return tags;
    }

    public static bool TryParseHead(ReadOnlySpan<byte> packet, out int channels, out int preSkip48k, out int inputSampleRate)
    {
        channels = preSkip48k = inputSampleRate = 0;
        if (packet.Length < 19 || !packet[..8].SequenceEqual("OpusHead"u8) || (packet[8] & 0xF0) != 0)
            return false;
        channels = packet[9];
        preSkip48k = BinaryPrimitives.ReadUInt16LittleEndian(packet[10..]);
        inputSampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(packet[12..]);
        return channels > 0;
    }

    public static bool IsTags(ReadOnlySpan<byte> packet) => packet.Length >= 8 && packet[..8].SequenceEqual("OpusTags"u8);

    /// <summary>Returns the user comments (<c>KEY=value</c>) of an OpusTags packet; empty when malformed.</summary>
    public static IReadOnlyList<string> ParseTagComments(ReadOnlySpan<byte> packet)
    {
        var result = new List<string>();
        if (!IsTags(packet) || packet.Length < 12) return result;
        int pos = 8;
        long vendorLength = BinaryPrimitives.ReadUInt32LittleEndian(packet[pos..]);
        pos += 4;
        if (vendorLength > packet.Length - pos - 4) return result;
        pos += (int)vendorLength;
        long count = BinaryPrimitives.ReadUInt32LittleEndian(packet[pos..]);
        pos += 4;
        for (long i = 0; i < count && pos + 4 <= packet.Length; i++)
        {
            long length = BinaryPrimitives.ReadUInt32LittleEndian(packet[pos..]);
            pos += 4;
            if (length > packet.Length - pos) break;
            result.Add(Encoding.UTF8.GetString(packet.Slice(pos, (int)length)));
            pos += (int)length;
        }
        return result;
    }

    /// <summary>The storage format recorded in the ENCODER_OPTIONS tag by <c>OggOpusRecordingWriter</c>.</summary>
    public static RecordingAudioFormat FormatFromTags(IReadOnlyList<string> comments)
    {
        const string key = "ENCODER_OPTIONS=";
        var spec = comments.FirstOrDefault(c => c.StartsWith(key, StringComparison.OrdinalIgnoreCase));
        try
        {
            if (spec is not null)
                return RecordingAudioFormat.Parse(spec[key.Length..]);
        }
        catch (FormatException) { }
        return new RecordingAudioFormat(RecordingAudioCodec.OggOpus);
    }
}
