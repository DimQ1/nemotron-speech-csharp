using System.Text;
using SpeechLib.ModelDownload;
using VoiceType.Uno.Localization;

namespace VoiceType.Uno.Services;

/// <summary>
/// The interface texts of a speech-model catalog entry (<see cref="ModelDescriptor"/>) in the current language. The library
/// keeps its English texts and knows nothing of <see cref="Loc"/>: the tagline and description are looked up by the entry's
/// folder name (<c>Models.Catalog.&lt;id&gt;.Tagline</c>), the labels and metric formats by fixed keys. A missing key falls back
/// to the library text, so English is identical either way.
/// </summary>
public static class ModelCatalogText
{
    /// <summary>The key segment of an entry: its folder name with every character outside <c>[A-Za-z0-9_.]</c> as <c>_</c>.</summary>
    public static string KeyOf(ModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var name = model.SubfolderName;
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' ? c : '_');
        return builder.ToString();
    }

    public static string Tagline(ModelDescriptor model) => Lookup(EntryKey(model, "Tagline"), model.Tagline);

    public static string Description(ModelDescriptor model) => Lookup(EntryKey(model, "Description"), model.Description);

    /// <summary>"4-bit (INT4)", spelled out like <see cref="ModelDescriptor.PrecisionSummary"/>.</summary>
    public static string Precision(ModelDescriptor model) => model.Precision switch
    {
        ModelPrecision.Fp32 => Loc.T("Models.Catalog.Precision.Fp32"),
        ModelPrecision.Int8 => Loc.T("Models.Catalog.Precision.Int8"),
        ModelPrecision.Int4 => Loc.T("Models.Catalog.Precision.Int4"),
        _ => model.PrecisionSummary,
    };

    /// <summary>"1.12s window" or "live streaming", like <see cref="ModelDescriptor.WindowSummary"/>.</summary>
    public static string Window(ModelDescriptor model) => model.ContextWindow is null
        ? Loc.T("Models.Catalog.Window.Live")
        : Loc.F("Models.Catalog.Window.Seconds", model.ContextWindow);

    /// <summary>The card title, like <see cref="ModelDescriptor.Title"/>.</summary>
    public static string Title(ModelDescriptor model) => model.ContextWindow is null
        ? Loc.F("Models.Catalog.Title", model.CommercialName, Precision(model))
        : Loc.F("Models.Catalog.TitleWithWindow", model.CommercialName, Precision(model), model.ContextWindow);

    public static string UseCase(ModelDescriptor model) => model.UseCase switch
    {
        ModelUseCase.FastDictation => Loc.T("Models.Catalog.UseCase.FastDictation"),
        ModelUseCase.HighQuality => Loc.T("Models.Catalog.UseCase.HighQuality"),
        ModelUseCase.Multilingual => Loc.T("Models.Catalog.UseCase.Multilingual"),
        _ => model.UseCaseText,
    };

    public static string Latency(ModelDescriptor model) => model.Latency switch
    {
        ModelLatencyProfile.Streaming => Loc.T("Models.Metrics.Latency.Streaming"),
        ModelLatencyProfile.Delayed => Loc.T("Models.Metrics.Latency.Delayed"),
        _ => model.LatencyText,
    };

    /// <summary>"WER 8.1%", like <see cref="ModelMetricsFormatter.FormatWer"/>.</summary>
    public static string Accuracy(ModelDescriptor model) => model.Research.Wer is { } wer
        ? Loc.F("Models.Metrics.Wer", wer.TotalPercent)
        : Loc.T("Models.Metrics.NoWer");

    /// <summary>"ru 5.8% / en 10.0%", like <see cref="ModelMetricsFormatter.FormatWerDetail"/>.</summary>
    public static string AccuracyDetail(ModelDescriptor model) =>
        model.Research.Wer is { RuPercent: { } ru, EnPercent: { } en }
            ? Loc.F("Models.Metrics.WerDetail", ru, en)
            : "";

    /// <summary>"≈5.4× real-time", like <see cref="ModelMetricsFormatter.FormatSpeed"/>.</summary>
    public static string Speed(ModelDescriptor model) => model.Research.Speed is { } speed
        ? Loc.F("Models.Metrics.Speed", speed.SpeedMultiplier)
        : Loc.T("Models.Metrics.NoSpeed");

    /// <summary>One line of every measured property, like <see cref="ModelDescriptor.MetricsLine"/>.</summary>
    public static string MetricsLine(ModelDescriptor model) => string.Join(" · ", new[]
    {
        Accuracy(model),
        Speed(model),
        model.SizeText,
        Latency(model),
        UseCase(model),
    }.Where(part => !string.IsNullOrWhiteSpace(part)));

    private static string EntryKey(ModelDescriptor model, string name) => "Models.Catalog." + KeyOf(model) + "." + name;

    private static string Lookup(string key, string fallback)
    {
        var text = Loc.T(key);
        return text == key ? fallback : text;
    }
}
