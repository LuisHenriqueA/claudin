using Claudin.Core;

namespace Claudin.Tests;

public sealed class PlanUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 18, 5, 0, TimeSpan.Zero);

    private static long Ms(DateTimeOffset at) => at.ToUnixTimeMilliseconds();

    [Fact]
    public void Version2_UsesTheMostRecentSample_InDisplayOrder()
    {
        var json = $$$"""
            {"version":2,"samples":[
              {"t":{{{Ms(Now.AddMinutes(-30))}}},"org":"a","u":{"fh":10,"sd":66}},
              {"t":{{{Ms(Now.AddMinutes(-2))}}},"org":"a","u":{"sd":68,"fh":24.5,"so":3,"om":9}},
              {"t":{{{Ms(Now.AddMinutes(-15))}}},"org":"a","u":{"fh":20,"sd":67}}
            ]}
            """;

        var r = PlanUsageReader.Parse(json, Now);

        Assert.Equal(PlanUsageStatus.Ok, r.Status);
        Assert.Equal(Now.AddMinutes(-2), r.Snapshot!.Timestamp);
        Assert.Equal(["fh", "sd", "so"], r.Snapshot.Windows.Select(w => w.Key)); // "om" é ignorada
        Assert.Equal(24.5, r.Snapshot.Windows[0].Percent);
        Assert.Equal("Sessão (5 h)", r.Snapshot.Windows[0].Label);
        Assert.All(r.Snapshot.Windows, w => Assert.False(w.Expired));
    }

    [Fact]
    public void Version1_ReadsValuesFromTheSampleItself()
    {
        var json = $$$"""{"version":1,"samples":[{"t":{{{Ms(Now.AddMinutes(-5))}}},"fh":40,"sd":null}]}""";

        var r = PlanUsageReader.Parse(json, Now);

        Assert.Equal(PlanUsageStatus.Ok, r.Status);
        var only = Assert.Single(r.Snapshot!.Windows);
        Assert.Equal(("fh", 40d), (only.Key, only.Percent));
    }

    [Fact]
    public void OldSample_MarksTheSessionWindowAsExpired_ButNotTheWeek()
    {
        var json = $$$"""{"version":2,"samples":[{"t":{{{Ms(Now.AddHours(-6))}}},"org":null,"u":{"fh":90,"sd":50,"xu":12}}]}""";

        var w = PlanUsageReader.Parse(json, Now).Snapshot!.Windows;

        Assert.True(w.Single(x => x.Key == "fh").Expired);
        Assert.False(w.Single(x => x.Key == "sd").Expired);
        Assert.False(w.Single(x => x.Key == "xu").Expired);
    }

    [Theory]
    [InlineData("""{"version":2,"samples":[]}""", PlanUsageStatus.Empty)]
    [InlineData("""{"version":3,"data":{}}""", PlanUsageStatus.Unreadable)]
    [InlineData("""[1,2]""", PlanUsageStatus.Unreadable)]
    [InlineData("""{"samples":[{"x":1}]}""", PlanUsageStatus.Unreadable)]
    [InlineData("""{ corrompido""", PlanUsageStatus.Unreadable)]
    public void UnexpectedContent_DoesNotThrow(string json, PlanUsageStatus expected)
    {
        Assert.Equal(expected, PlanUsageReader.Parse(json, Now).Status);
    }

    [Fact]
    public void Read_MissingFile_IsNoFile()
    {
        using var dir = new TempDir();

        Assert.Equal(PlanUsageStatus.NoFile, PlanUsageReader.Read(dir.Path, Now).Status);
        Assert.Equal(PlanUsageStatus.NoFile, PlanUsageReader.Read(dir.Combine("nao-existe"), Now).Status);
    }

    [Fact]
    public void Read_FileInDataDir()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine(PlanUsageReader.FileName),
            $$$"""{"version":2,"samples":[{"t":{{{Ms(Now)}}},"org":"a","u":{"fh":1,"sd":2}}]}""");

        var r = PlanUsageReader.Read(dir.Path, Now);

        Assert.Equal(PlanUsageStatus.Ok, r.Status);
        Assert.Equal(2, r.Snapshot!.Windows.Count);
    }

    [Theory]
    [InlineData(20, "agora")]
    [InlineData(60 * 3, "há 3 min")]
    [InlineData(60 * 60 * 2 + 59, "há 2 h")]
    [InlineData(60 * 60 * 24, "há 1 dia")]
    [InlineData(60 * 60 * 24 * 3, "há 3 dias")]
    public void FormatAge(int seconds, string expected)
    {
        Assert.Equal(expected, PlanUsageReader.FormatAge(TimeSpan.FromSeconds(seconds)));
    }
}
