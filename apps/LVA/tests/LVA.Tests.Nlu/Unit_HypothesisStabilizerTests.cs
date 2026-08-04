using LVA.Nlu.Stabilization;
using Xunit;

namespace LVA.Tests.Nlu;

public class Unit_HypothesisStabilizerTests
{
    [Fact]
    public void StablePrefix_ShouldGrowOnlyAfterNUnchangedUpdates()
    {
        var s = new HypothesisStabilizer(stableAfter: 3);

        s.Update("как обновить");
        s.Update("как обновить драйвер");
        Assert.Equal("", s.StablePrefix);           // изменения — префикс не растёт

        s.Update("как обновить драйвер");
        s.Update("как обновить драйвер");
        s.Update("как обновить драйвер");           // 4 неизменных подряд (count >= 3)
        Assert.True(s.IsStable);
    }

    [Fact]
    public void LongestCommonPrefix_ShouldBeCaseInsensitive()
    {
        Assert.Equal("Как обн", HypothesisStabilizer.LongestCommonPrefix("Как обновить", "как обновить драйвер".Replace("новить", "НОВИТЬ").Replace("Как", "Как"))[..7]);
        Assert.Equal("abc", HypothesisStabilizer.LongestCommonPrefix("abcdef", "ABCxyz"));
    }

    [Fact]
    public void Reset_ShouldClearState()
    {
        var s = new HypothesisStabilizer();
        s.Update("текст");
        s.Reset();
        Assert.Equal("", s.StablePrefix);
        Assert.False(s.IsStable);
    }
}
