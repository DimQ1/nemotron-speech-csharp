namespace SpeechLib.ModelDownload;

/// <summary>
/// Catalog-relative 1..5 star ratings for a model. Stars are computed by
/// comparing the model with the other catalog entries that have the same kind
/// of measurement, so a card can show "5 stars for speed, 2 for size" without
/// the user having to compare raw numbers across families.
/// </summary>
public sealed record ModelRating(int Accuracy, int Speed, int Size)
{
    public const int MaxStars = 5;

    /// <summary>Neutral rating used when a metric is missing or incomparable.</summary>
    public static readonly ModelRating Neutral = new(3, 3, 3);

    public string AccuracyStars => Format(Accuracy);

    public string SpeedStars => Format(Speed);

    public string SizeStars => Format(Size);

    /// <summary>Renders stars as "★★★☆☆".</summary>
    public static string Format(int stars)
    {
        var filled = Math.Clamp(stars, 0, MaxStars);
        return new string('★', filled) + new string('☆', MaxStars - filled);
    }
}

/// <summary>
/// The catalog ranked against itself: per-model star ratings plus the winners of
/// each category ("most accurate", "fastest", "smallest download") so the UI can
/// badge the models worth downloading.
/// </summary>
public sealed class ModelRankingTable
{
    private readonly IReadOnlyDictionary<string, ModelRating> _ratings;

    private ModelRankingTable(
        IReadOnlyDictionary<string, ModelRating> ratings,
        ModelDescriptor? mostAccurate,
        ModelDescriptor? fastest,
        ModelDescriptor? smallest,
        int ratedCount)
    {
        _ratings = ratings;
        MostAccurate = mostAccurate;
        Fastest = fastest;
        Smallest = smallest;
        RatedCount = ratedCount;
    }

    /// <summary>Highest accuracy on the shared test set (lowest WER), or null without measurements.</summary>
    public ModelDescriptor? MostAccurate { get; }

    /// <summary>Least CPU per audio second (highest real-time multiplier), or null.</summary>
    public ModelDescriptor? Fastest { get; }

    /// <summary>Smallest download.</summary>
    public ModelDescriptor? Smallest { get; }

    /// <summary>How many catalog entries carry accuracy/speed measurements.</summary>
    public int RatedCount { get; }

    /// <summary>Builds the ranking table for <paramref name="models"/>.</summary>
    public static ModelRankingTable For(IReadOnlyList<ModelDescriptor> models)
    {
        var withWer = models.Where(m => m.Research.Wer is not null).ToList();
        var withSpeed = models.Where(m => m.Research.Speed is not null).ToList();

        var werBounds = Bounds(withWer.Select(m => m.Research.Wer!.TotalPercent));
        var speedBounds = Bounds(withSpeed.Select(m => m.Research.Speed!.RealTimeFactor));
        var sizeBounds = Bounds(models.Select(m => (double)m.SizeBytes));

        var ratings = new Dictionary<string, ModelRating>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
        {
            var accuracy = model.Research.Wer is null
                ? ModelRating.Neutral.Accuracy
                : Stars(model.Research.Wer.TotalPercent, werBounds);
            var speed = model.Research.Speed is null
                ? ModelRating.Neutral.Speed
                : Stars(model.Research.Speed.RealTimeFactor, speedBounds);
            var size = Stars(model.SizeBytes, sizeBounds);

            ratings[model.SubfolderName] = new ModelRating(accuracy, speed, size);
        }

        return new ModelRankingTable(
            ratings,
            PickLowest(withWer, m => m.Research.Wer!.TotalPercent),
            PickHighest(withSpeed, m => m.Research.Speed!.SpeedMultiplier),
            models.Count == 0 ? null : models.MinBy(m => m.SizeBytes),
            withWer.Count);
    }

    /// <summary>Star rating of a model, or <see cref="ModelRating.Neutral"/> when unknown.</summary>
    public ModelRating Rate(ModelDescriptor model)
        => _ratings.TryGetValue(model.SubfolderName, out var rating) ? rating : ModelRating.Neutral;

    /// <summary>True for the catalog entry flagged as the everyday recommendation.</summary>
    public static bool IsRecommended(ModelDescriptor model) => model.IsRecommended;

    /// <summary>
    /// Maps a raw value onto 1..5 stars, where the lowest value in the catalog
    /// gets 5 stars (WER, real-time factor and size are all "smaller is better").
    /// Equal or single-valued ranges yield the neutral 3 stars.
    /// </summary>
    private static int Stars(double value, (double Best, double Worst) bounds)
    {
        if (bounds.Worst <= bounds.Best)
            return ModelRating.Neutral.Accuracy;

        var ratio = (bounds.Worst - value) / (bounds.Worst - bounds.Best);
        return Math.Clamp((int)Math.Round(1 + ratio * (ModelRating.MaxStars - 1)), 1, ModelRating.MaxStars);
    }

    private static (double Best, double Worst) Bounds(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? (0, 0) : (list.Min(), list.Max());
    }

    private static ModelDescriptor? PickLowest(
        IReadOnlyList<ModelDescriptor> models, Func<ModelDescriptor, double> selector)
        => models.Count == 0 ? null : models.MinBy(selector);

    private static ModelDescriptor? PickHighest(
        IReadOnlyList<ModelDescriptor> models, Func<ModelDescriptor, double> selector)
        => models.Count == 0 ? null : models.MaxBy(selector);
}
