using SpeechLib.ModelDownload;
using Xunit;

namespace SpeechLib.Tests;

/// <summary>
/// The model catalog is what the Uno model manager and the WinUI downloader show
/// to users, so its naming and the derived star ratings have to stay unambiguous:
/// every variant must be distinguishable by name alone and the "best" badges must
/// point at the models the numbers actually favour.
/// </summary>
public sealed class Unit_ModelCatalogPresentationTests
{
    [Fact]
    public void EveryModel_HasAUniqueTitle()
    {
        var titles = ModelCatalog.Models.Select(m => m.Title).ToList();

        Assert.Equal(titles.Count, titles.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryModel_TitleSpellsOutThePrecision()
    {
        foreach (var model in ModelCatalog.Models)
        {
            Assert.Contains(
                model.PrecisionSummary,
                new[] { "4-bit (INT4)", "8-bit (INT8)", "full precision (FP32)" });
            Assert.Contains(model.PrecisionSummary, model.Title, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryModel_HasAUniqueSubfolder()
    {
        // The model manager detects installed models and de-duplicates downloads
        // by this folder name, so two catalog entries may never share one.
        var folders = ModelCatalog.Models.Select(m => m.SubfolderName).ToList();

        Assert.Equal(folders.Count, folders.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ParakeetVariants_ShareARepoButGetDistinctFolders()
    {
        var parakeet = ModelCatalog.Models
            .Where(m => m.CommercialName.StartsWith("Parakeet", StringComparison.Ordinal))
            .ToList();

        Assert.True(parakeet.Count >= 2);
        Assert.Single(parakeet.Select(m => m.RepoId).Distinct(StringComparer.Ordinal));
        Assert.Equal(parakeet.Count, parakeet.Select(m => m.SubfolderName).Distinct().Count());
    }

    [Fact]
    public void OrderedModels_PutTheRecommendedModelFirst()
    {
        var first = ModelCatalog.OrderedModels[0];

        Assert.Equal(ModelCatalog.Recommended.SubfolderName, first.SubfolderName);
    }

    [Fact]
    public void DisplayName_AlwaysCarriesTheTitleAndTheSize()
    {
        foreach (var model in ModelCatalog.Models)
        {
            Assert.Contains(model.Title, model.DisplayName, StringComparison.Ordinal);
            Assert.Contains(model.SizeText, model.DisplayName, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ModelFamiliesWithSeveralWindows_SayWhichWindowTheyUse()
    {
        foreach (var model in ModelCatalog.Models.Where(m => m.ContextWindow is not null))
            Assert.Contains(model.ContextWindow!, model.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Rating_GivesFiveStarsToTheMeasuredLeaderOfEachCategory()
    {
        var ranking = ModelCatalog.Ranking;

        var mostAccurate = Assert.IsType<ModelDescriptor>(ranking.MostAccurate);
        var fastest = Assert.IsType<ModelDescriptor>(ranking.Fastest);
        var smallest = Assert.IsType<ModelDescriptor>(ranking.Smallest);

        Assert.Equal(5, ranking.Rate(mostAccurate).Accuracy);
        Assert.Equal(5, ranking.Rate(fastest).Speed);
        Assert.Equal(5, ranking.Rate(smallest).Size);
    }

    [Fact]
    public void Rating_IsNeutralWhenAMetricWasNeverMeasured()
    {
        var unmeasured = ModelCatalog.Models.First(m => m.Research.Wer is null);

        Assert.Equal(ModelRating.Neutral.Accuracy, ModelCatalog.Ranking.Rate(unmeasured).Accuracy);
    }

    [Fact]
    public void Stars_AreAlwaysFiveCharacters()
    {
        Assert.Equal("★★★★★", ModelRating.Format(5));
        Assert.Equal("☆☆☆☆☆", ModelRating.Format(0));
        Assert.Equal("★★★☆☆", ModelRating.Format(3));
        Assert.Equal(5, ModelRating.Format(9).Length);
        Assert.Equal(5, ModelRating.Format(-3).Length);
    }

    [Fact]
    public void DescribeFolder_TranslatesKnownFoldersAndKeepsUnknownOnes()
    {
        var known = ModelCatalog.Models[0];

        Assert.Equal(known.Title, ModelCatalog.DescribeFolder(known.SubfolderName));
        Assert.Equal("my-custom-model", ModelCatalog.DescribeFolder("my-custom-model"));
        Assert.Equal("no model selected", ModelCatalog.DescribeFolder(""));
        Assert.Equal("no model selected", ModelCatalog.DescribeFolder(null));
    }

    [Fact]
    public void Metrics_UseMeasuredNumbersOrSayTheyAreMissing()
    {
        foreach (var model in ModelCatalog.Models)
        {
            if (model.Research.Wer is null)
                Assert.Equal("No WER test yet", model.AccuracyText);
            else
                Assert.Contains("WER", model.AccuracyText, StringComparison.Ordinal);

            if (model.Research.Speed is null)
                Assert.Equal("No speed test yet", model.SpeedText);
            else
                Assert.Contains("real-time", model.SpeedText, StringComparison.Ordinal);
        }
    }
}
