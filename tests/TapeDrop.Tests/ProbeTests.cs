using TapeDrop.Core;
using TapeDrop.Probe;

namespace TapeDrop.Tests;

public class ProbeArgsTests
{
    [Fact]
    public void No_arguments_means_scan()
    {
        var a = ProbeArgs.Parse(Array.Empty<string>());
        Assert.Equal("scan", a.Command);
    }

    [Fact]
    public void Command_options_and_flags_are_parsed()
    {
        var a = ProbeArgs.Parse(new[] { "copy-test", "--source", @"E:\Photos", "--target", @"L:\", "--gb", "5", "--verify", "--yes" });

        Assert.Equal("copy-test", a.Command);
        Assert.Equal(@"E:\Photos", a.Get("source"));
        Assert.Equal(@"L:\", a.Get("target"));
        Assert.Equal("5", a.Get("gb"));
        Assert.True(a.Has("verify"));
        Assert.True(a.Has("yes"));
        Assert.False(a.Has("keep"));
    }

    [Fact]
    public void Option_names_are_case_insensitive()
    {
        var a = ProbeArgs.Parse(new[] { "read-test", "--SOURCE", "x" });
        Assert.Equal("x", a.Get("source"));
    }

    [Fact]
    public void Option_without_a_value_is_an_error()
    {
        Assert.Throws<ArgumentException>(() => ProbeArgs.Parse(new[] { "read-test", "--source" }));
    }

    [Fact]
    public void Stray_argument_is_an_error()
    {
        Assert.Throws<ArgumentException>(() => ProbeArgs.Parse(new[] { "scan", "oops" }));
    }
}

public class ProbePlannerTests
{
    private static CopyPlan PlanOf(params long[] sizes) => new(
        sizes.Select((s, i) => new PlannedFile($"/src/{i}", $"f{i}", s, DateTime.UtcNow)).ToList(),
        sizes.Sum(),
        Array.Empty<string>());

    [Fact]
    public void Limit_stops_once_the_target_is_reached_and_keeps_whole_files()
    {
        var limited = ProbePlanner.Limit(PlanOf(400, 400, 400, 400), 1000);

        Assert.Equal(3, limited.Files.Count);   // 400 + 400 = 800 < 1000, so the third file is taken
        Assert.Equal(1200, limited.TotalBytes);
    }

    [Fact]
    public void Limit_larger_than_the_plan_returns_everything()
    {
        var limited = ProbePlanner.Limit(PlanOf(10, 20), 1_000_000);
        Assert.Equal(2, limited.Files.Count);
        Assert.Equal(30, limited.TotalBytes);
    }

    [Fact]
    public void Limit_of_an_empty_plan_is_empty()
    {
        var limited = ProbePlanner.Limit(PlanOf(), 1000);
        Assert.Empty(limited.Files);
    }
}

public class RateSamplerTests
{
    [Fact]
    public void Summarize_of_nothing_is_null()
    {
        Assert.Null(RateSampler.Summarize(Array.Empty<double>()));
    }

    [Fact]
    public void Summarize_odd_count_uses_the_middle_value()
    {
        var s = RateSampler.Summarize(new double[] { 300, 100, 200 })!;
        Assert.Equal(100, s.Min);
        Assert.Equal(200, s.Median);
        Assert.Equal(300, s.Max);
    }

    [Fact]
    public void Summarize_even_count_averages_the_two_middle_values()
    {
        var s = RateSampler.Summarize(new double[] { 100, 200, 300, 400 })!;
        Assert.Equal(250, s.Median);
    }

    [Fact]
    public async Task Windows_are_recorded_as_time_passes()
    {
        var sampler = new RateSampler(TimeSpan.FromMilliseconds(30));
        await Task.Delay(60);
        sampler.Mark(6_000_000);

        var rate = Assert.Single(sampler.WindowRates);
        Assert.True(rate > 0);
    }
}
