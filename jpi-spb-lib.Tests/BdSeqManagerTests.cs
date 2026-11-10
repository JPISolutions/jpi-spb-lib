namespace SparkplugB.Publisher.Tests;

public class BdSeqManagerTests
{
    private static string? NoEnvironment(string _) => null;

    [Fact]
    public void Default_OnWindows_IsUnderProgramData()
    {
        var directory = BdSeqManager.DefaultDirectory(isWindows: true, NoEnvironment, @"C:\ProgramData", @"C:\Users\svc");

        Assert.Equal(Path.Combine(@"C:\ProgramData", "JPI Solutions", "Sparkplug"), directory);
    }

    [Fact]
    public void Default_OnLinux_UsesSystemdStateDirectory_WhenSet()
    {
        string? Environment(string name) => name == "STATE_DIRECTORY" ? "/var/lib/sysmon:/var/lib/other" : "/xdg";

        Assert.Equal("/var/lib/sysmon", BdSeqManager.DefaultDirectory(false, Environment, "/usr/share", "/home/svc"));
    }

    [Fact]
    public void Default_OnLinux_ThenXdgStateHome_ThenHome()
    {
        string? Xdg(string name) => name == "XDG_STATE_HOME" ? "/home/svc/.state" : null;

        Assert.Equal(Path.Combine("/home/svc/.state", "jpi-sparkplug"),
            BdSeqManager.DefaultDirectory(false, Xdg, "/usr/share", "/home/svc"));
        Assert.Equal(Path.Combine("/home/svc", ".local", "state", "jpi-sparkplug"),
            BdSeqManager.DefaultDirectory(false, NoEnvironment, "/usr/share", "/home/svc"));
    }

    [Fact]
    public void DefaultFile_IsPerEdgeNode_AndSafeAsAFileName()
    {
        Assert.Equal("JPI Group.Box_1.bdseq", BdSeqManager.FileName("JPI Group", "Box:1"));

        var manager = new BdSeqManager(null, "G", "N");
        Assert.Equal(Path.Combine(BdSeqManager.DefaultDirectory(), "G.N.bdseq"), manager.FilePath);
    }

    [Fact]
    public void ConfiguredPath_RelativeIsUnderTheApplicationFolder_AbsoluteIsKept()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "state.txt"), new BdSeqManager("state.txt", "G", "N").FilePath);

        var absolute = Path.Combine(Path.GetTempPath(), "state.txt");
        Assert.Equal(absolute, new BdSeqManager(absolute, "G", "N").FilePath);
    }

    [Fact]
    public void LoadLast_ReportsAnUnusableFile_InsteadOfThrowing()
    {
        var file = Path.Combine(Path.GetTempPath(), $"bdseq-{Guid.NewGuid():N}.txt");
        var manager = new BdSeqManager(file, "G", "N");
        try
        {
            Assert.Null(manager.LoadLast(out var missing));
            Assert.Null(missing); // no file yet is not a problem

            File.WriteAllText(file, "garbage");
            Assert.Null(manager.LoadLast(out var corrupt));
            Assert.Contains("garbage", corrupt);

            Assert.True(manager.TrySave(42, out _));
            Assert.Equal(42UL, manager.LoadLast(out var none));
            Assert.Null(none);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void TrySave_ReportsAFailure_InsteadOfThrowing()
    {
        var blocker = Path.Combine(Path.GetTempPath(), $"bdseq-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "a file where a folder is needed");
        try
        {
            var manager = new BdSeqManager(Path.Combine(blocker, "state.txt"), "G", "N");

            Assert.False(manager.TrySave(1, out var problem));
            Assert.False(string.IsNullOrEmpty(problem));
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Theory]
    [InlineData(null, 0UL)]
    [InlineData(0UL, 1UL)]
    [InlineData(254UL, 255UL)]
    [InlineData(255UL, 0UL)]
    [InlineData(300UL, 45UL)] // a value from before the wrap was enforced
    public void Next_StartsAtZero_AndWrapsAfter255(ulong? last, ulong expected) =>
        Assert.Equal(expected, BdSeqManager.Next(last));
}
