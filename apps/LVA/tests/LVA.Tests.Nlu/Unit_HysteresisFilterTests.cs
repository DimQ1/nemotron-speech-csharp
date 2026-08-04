using LVA.Nlu.Intents;
using Xunit;

namespace LVA.Tests.Nlu;

public class Unit_HysteresisFilterTests
{
    [Fact]
    public void ShouldEnterStateAfterMofNHighScores()
    {
        var h = new HysteresisFilter(m: 3, n: 5, threshold: 0.85f);

        h.Update(0.9f);  // 1
        Assert.False(h.IsActive);
        h.Update(0.1f);  // flicker
        Assert.False(h.IsActive);
        h.Update(0.9f);  // 2
        Assert.False(h.IsActive);
        h.Update(0.9f);  // 3 of 4 → activate
        Assert.True(h.IsActive);
    }

    [Fact]
    public void ShouldNotFlickerOnSingleLowScore()
    {
        var h = new HysteresisFilter(m: 3, n: 5, threshold: 0.85f);
        foreach (var s in new[] { 0.9f, 0.9f, 0.9f })
            h.Update(s);
        Assert.True(h.IsActive);

        h.Update(0.1f);  // 3 of 4 still ≥ M in window of ≤5
        Assert.True(h.IsActive);

        h.Update(0.1f);
        h.Update(0.1f);  // now only 2 of 5
        h.Update(0.1f);
        Assert.False(h.IsActive);
    }

    [Fact]
    public void Reset_ShouldClearWindow()
    {
        var h = new HysteresisFilter(m: 1, n: 3, threshold: 0.5f);
        h.Update(0.9f);
        Assert.True(h.IsActive);
        h.Reset();
        Assert.False(h.IsActive);
    }
}
