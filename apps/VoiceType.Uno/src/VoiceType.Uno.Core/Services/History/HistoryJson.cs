using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace VoiceType.Uno.History;

/// <summary>
/// Source-generated serialization metadata for every history file
/// (manifest, transcript, translation, chat, job queue). camelCase, indented,
/// tolerant of comments and trailing commas; unknown fields are kept in each
/// type's <c>ExtensionData</c> so a file written by a newer app round-trips.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(RecordingManifest))]
[JsonSerializable(typeof(TranscriptDocument))]
[JsonSerializable(typeof(TranslationDocument))]
[JsonSerializable(typeof(ChatRecord))]
[JsonSerializable(typeof(JobQueueDocument))]
[JsonSerializable(typeof(HistoryJob))]
[JsonSerializable(typeof(TranscriptVersion))]
[JsonSerializable(typeof(TranslationInfo))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(JobParameters))]
[JsonSerializable(typeof(JobCondition))]
internal partial class HistoryJsonContext : JsonSerializerContext;

/// <summary>Atomic JSON file I/O shared by the history stores.</summary>
internal static class HistoryJson
{
    /// <summary>UTF-8 without BOM.</summary>
    public static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static T Read<T>(string path, JsonTypeInfo<T> typeInfo)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, typeInfo)
            ?? throw new JsonException($"'{Path.GetFileName(path)}' contains null.");
    }

    /// <summary>
    /// Writes to a sibling temp file and moves it over the target, so a crash leaves
    /// either the old or the new file, never a torn one.
    /// </summary>
    public static void WriteAtomic<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(value, typeInfo);
        WriteTextAtomic(path, json);
    }

    public static void WriteTextAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, content, Utf8);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (IOException) { }
            }
        }
    }

    /// <summary>Deep copy through the serializer (keeps extension data).</summary>
    public static T Clone<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(value, typeInfo), typeInfo)!;
}

/// <summary>
/// Helpers for the open <c>settings</c> dictionaries (recognition settings that
/// affect the result). Values are kept as raw JSON so any value a newer app writes
/// still loads.
/// </summary>
public static class JsonValues
{
    /// <summary>Converts a primitive (bool, number, string, null) to a <see cref="JsonElement"/>.</summary>
    public static JsonElement From(object? value)
    {
        var literal = value switch
        {
            null => "null",
            bool b => b ? "true" : "false",
            string s => JsonSerializer.Serialize(s, HistoryPrimitiveContext.Default.String),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            IFormattable n when IsInteger(value) => n.ToString(null, CultureInfo.InvariantCulture),
            _ => throw new ArgumentException($"Unsupported settings value type {value.GetType().Name}.", nameof(value)),
        };
        using var doc = JsonDocument.Parse(literal);
        return doc.RootElement.Clone();
    }

    /// <summary>Builds a settings dictionary from name/value pairs.</summary>
    public static Dictionary<string, JsonElement> Settings(params (string Name, object? Value)[] values)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
            result[name] = From(value);
        return result;
    }

    /// <summary>Invariant display text of a settings value (<c>true</c>, <c>1.1</c>, <c>cpu</c>).</summary>
    public static string ToDisplay(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => value.GetRawText(),
    };

    private static bool IsInteger(object value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong;
}

[JsonSerializable(typeof(string))]
internal partial class HistoryPrimitiveContext : JsonSerializerContext;

/// <summary>
/// Enum converter that writes camelCase names and never fails on read: an unknown
/// name (written by a newer app) becomes <c>default(TEnum)</c>, so one unexpected
/// value cannot make a whole file unloadable.
/// </summary>
public sealed class LenientEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                var text = reader.GetString();
                if (!string.IsNullOrEmpty(text)
                    && !char.IsDigit(text[0]) && text[0] != '-'
                    && Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed)
                    && Enum.IsDefined(parsed))
                    return parsed;
                return default;
            case JsonTokenType.Number:
                if (reader.TryGetInt32(out var number))
                {
                    var value = (TEnum)Enum.ToObject(typeof(TEnum), number);
                    if (Enum.IsDefined(value))
                        return value;
                }
                return default;
            default:
                reader.Skip();
                return default;
        }
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
}
