namespace PortableMSVC.Tests;

[TestClass]
public sealed class CliTests
{
    [TestMethod]
    public async Task InstallDryRunReturnsPlan()
    {
        var exitCode = await Cli.RunAsync(["install", "--dry-run", "--vs", "2022", "--host", "x64", "--target", "x64", "--cache", ManifestTestData.ManifestDirectory]);

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task PublishedCliDoesNotExposeTestCommand()
    {
        var exitCode = await Cli.RunAsync(["test", "matrix", "--cache", ManifestTestData.ManifestDirectory]);

        Assert.AreEqual(2, exitCode);
    }

    [TestMethod]
    public async Task HelpCommandAliasPrintsHelp()
    {
        var exitCode = await Cli.RunAsync(["help"]);

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ListPrintsVersionsWithLatestMarked()
    {
        TextWriter originalOut = Console.Out;
        using StringWriter output = new StringWriter();
        try
        {
            Console.SetOut(output);

            var exitCode = await Cli.RunAsync(["list", "--vs", "2022", "--cache", ManifestTestData.ManifestDirectory]);

            Assert.AreEqual(0, exitCode);
            var text = output.ToString();
            StringAssert.Contains(text, "MSVC 工具集版本");
            StringAssert.Contains(text, "Windows SDK 版本");
            StringAssert.Contains(text, "MSVC Redist 版本");
            StringAssert.Contains(text, "14.29.16.11");
            StringAssert.Contains(text, "10.0.19041");
            StringAssert.Contains(text, "(latest)");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [TestMethod]
    public void CorePlanIncludesOfficialVcvarsPackages()
    {
        var index = ManifestTestData.Load("2022");
        var plan = new PlanBuilder().Build(index, new PlanRequest(
            "2022",
            "14.44",
            "26100",
            "14.44",
            Architecture.X64,
            [Architecture.X64],
            WithRuntime: false));
        var roots = plan.RootPackages.Select(x => x.Id).ToList();

        Assert.IsTrue(plan.Success, string.Join(Environment.NewLine, plan.Issues.Select(x => x.Message)));
        CollectionAssert.Contains(roots, "Microsoft.VisualCpp.Tools.Core.x86");
        CollectionAssert.Contains(roots, "Microsoft.VisualCpp.Tools.HostX64.TargetX64");
        CollectionAssert.Contains(roots, "Microsoft.VisualStudio.VC.DevCmd");
        CollectionAssert.Contains(roots, "Microsoft.VisualStudio.VC.vcvars");
        CollectionAssert.Contains(roots, "Microsoft.VisualStudio.VsDevCmd.Core.WinSdk");
    }

    [TestMethod]
    public async Task UnknownOptionsAreRejected()
    {
        var exitCode = await Cli.RunAsync(["plan", "--unknown-option", "--vs", "2022", "--cache", ManifestTestData.ManifestDirectory]);

        Assert.AreEqual(2, exitCode);
    }

    [TestMethod]
    public async Task Arm32HostIsRejectedBeforePlanning()
    {
        var exitCode = await Cli.RunAsync(["plan", "--vs", "2019", "--host", "arm", "--target", "arm64", "--cache", ManifestTestData.ManifestDirectory]);

        Assert.AreEqual(2, exitCode);
    }
}
