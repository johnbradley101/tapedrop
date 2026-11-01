using TapeDrop.Core;

namespace TapeDrop.Tests;

public class NameSanitizerTests
{
    [Theory]
    [InlineData("ok.txt", "ok.txt")]
    [InlineData("a:b", "a_b")]
    [InlineData("what?.txt", "what_.txt")]
    [InlineData("trail. ", "trail")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("com1", "_com1")]
    [InlineData(".gitignore", ".gitignore")]
    [InlineData("", "_")]
    public void Sanitize_produces_cross_platform_safe_names(string input, string expected)
    {
        Assert.Equal(expected, NameSanitizer.Sanitize(input));
    }
}

public class JobPlannerTests
{
    [Fact]
    public void Folder_keeps_structure_under_its_own_name_and_totals_sizes()
    {
        using var t = new TempDir();
        t.Make("photos/a.txt", new byte[10]);
        t.Make("photos/sub/b.bin", new byte[25]);

        var plan = JobPlanner.Plan(new[] { System.IO.Path.Combine(t.Root, "photos") });

        Assert.Equal(new[] { "photos/a.txt", "photos/sub/b.bin" }, plan.Files.Select(f => f.RelativePath).ToArray());
        Assert.Equal(35, plan.TotalBytes);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Single_file_uses_just_its_file_name()
    {
        using var t = new TempDir();
        var file = t.Make("deep/dir/movie.mov", new byte[7]);

        var plan = JobPlanner.Plan(new[] { file });

        Assert.Single(plan.Files);
        Assert.Equal("movie.mov", plan.Files[0].RelativePath);
    }

    [Fact]
    public void Missing_path_is_reported_as_a_warning()
    {
        using var t = new TempDir();
        var plan = JobPlanner.Plan(new[] { System.IO.Path.Combine(t.Root, "nope") });

        Assert.Empty(plan.Files);
        Assert.Single(plan.Warnings);
    }

    [Fact]
    public void Files_with_clashing_names_are_renamed_not_overwritten()
    {
        using var t = new TempDir();
        var one = t.Make("one/x.txt", new byte[1]);
        var two = t.Make("two/x.txt", new byte[2]);

        var plan = JobPlanner.Plan(new[] { one, two });

        var names = plan.Files.Select(f => f.RelativePath).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "x (2).txt", "x.txt" }, names);
        Assert.Single(plan.Warnings);
    }

    [Fact]
    public void Illegal_characters_in_folder_names_are_sanitised()
    {
        using var t = new TempDir();
        // ':' and '?' are legal on Linux but not on Windows tapes; the planner must replace them.
        if (OperatingSystem.IsWindows()) return; // cannot create such a folder on Windows
        t.Make("we?ird/file.txt", new byte[3]);

        var plan = JobPlanner.Plan(new[] { System.IO.Path.Combine(t.Root, "we?ird") });

        Assert.Equal("we_ird/file.txt", plan.Files.Single().RelativePath);
    }
}
