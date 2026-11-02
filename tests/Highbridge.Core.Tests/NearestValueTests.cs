using Highbridge.Scanning;

namespace Highbridge.Core.Tests;

public class NearestValueTests
{
    [Theory]
    [InlineData(200, 200)]
    [InlineData(250, 300)]
    [InlineData(240, 200)]
    [InlineData(1200, 600)]
    [InlineData(10, 75)]
    public void Picks_the_closest_listed_value(double wanted, double expected)
    {
        Assert.Equal(expected, NearestValue.FromList([75, 100, 150, 200, 300, 600], wanted));
    }

    [Fact]
    public void Empty_list_has_no_nearest_value()
    {
        Assert.Null(NearestValue.FromList([], 200));
    }

    [Theory]
    [InlineData(50, 1200, 1, 200, 200)]
    [InlineData(100, 1200, 100, 250, 300)]
    [InlineData(75, 600, 25, 2400, 600)]
    [InlineData(75, 600, 25, 10, 75)]
    [InlineData(75, 610, 50, 610, 575)]
    [InlineData(100, 100, 0, 200, 100)]
    public void Snaps_to_a_range_step(double min, double max, double step, double wanted, double expected)
    {
        Assert.Equal(expected, NearestValue.FromRange(min, max, step, wanted));
    }
}
