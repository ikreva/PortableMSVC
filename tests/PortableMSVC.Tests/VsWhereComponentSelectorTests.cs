namespace PortableMSVC.Tests;

[TestClass]
public sealed class VsWhereComponentSelectorTests
{
    [TestMethod]
    public void Arm64HostArm64TargetSelectsOnlyArm64ToolsComponent()
    {
        IReadOnlyList<VsWherePackage> packages = Select(Architecture.Arm64, [Architecture.Arm64]);
        List<string> ids = packages.Select(x => x.Id).ToList();

        CollectionAssert.AreEqual(
            new[] { "Microsoft.VisualStudio.Component.VC.Tools.ARM64" },
            ids);
        Assert.AreEqual("18.5.11709.182", packages.Single().Version);
    }

    [TestMethod]
    public void Arm64HostX64TargetSelectsArm64AndX86X64Components()
    {
        IReadOnlyList<VsWherePackage> packages = Select(Architecture.Arm64, [Architecture.X64]);
        List<string> ids = packages.Select(x => x.Id).ToList();

        CollectionAssert.AreEqual(
            new[]
            {
                "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
                "Microsoft.VisualStudio.Component.VC.Tools.ARM64"
            },
            ids);
    }

    [TestMethod]
    public void X64HostArm64TargetSelectsX86X64AndArm64Components()
    {
        IReadOnlyList<VsWherePackage> packages = Select(Architecture.X64, [Architecture.Arm64]);
        List<string> ids = packages.Select(x => x.Id).ToList();

        CollectionAssert.AreEqual(
            new[]
            {
                "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
                "Microsoft.VisualStudio.Component.VC.Tools.ARM64"
            },
            ids);
    }

    [TestMethod]
    public void X64HostArmTargetSelectsX86X64AndArmComponents()
    {
        IReadOnlyList<VsWherePackage> packages = Select(Architecture.X64, [Architecture.Arm]);
        List<string> ids = packages.Select(x => x.Id).ToList();

        CollectionAssert.AreEqual(
            new[]
            {
                "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
                "Microsoft.VisualStudio.Component.VC.Tools.ARM"
            },
            ids);
    }

    [TestMethod]
    public void MissingComponentPackageIsNotInvented()
    {
        PackageIndex index = CreateIndex(includeArm64: false);
        InstallPlan plan = CreatePlan(Architecture.Arm64, [Architecture.Arm64]);

        IReadOnlyList<VsWherePackage> packages = VsWhereComponentSelector.SelectVcToolComponents(index, plan);

        Assert.IsEmpty(packages);
    }

    private static IReadOnlyList<VsWherePackage> Select(Architecture host, IReadOnlyList<Architecture> targets)
    {
        return VsWhereComponentSelector.SelectVcToolComponents(CreateIndex(includeArm64: true), CreatePlan(host, targets));
    }

    private static PackageIndex CreateIndex(bool includeArm64)
    {
        List<PackageInfo> packages =
        [
            Component("Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "18.5.11709.181"),
            Component("Microsoft.VisualStudio.Component.VC.Tools.ARM", "18.5.11709.180")
        ];
        if (includeArm64)
        {
            packages.Add(Component("Microsoft.VisualStudio.Component.VC.Tools.ARM64", "18.5.11709.182"));
        }

        return new PackageIndex(
            "2026",
            "18.5.0",
            "18.5.11709.182",
            "18.5.11709.182+0",
            "Dev18",
            "18",
            "VisualStudio",
            packages);
    }

    private static PackageInfo Component(string id, string version)
    {
        return new PackageInfo(id, id.ToLowerInvariant(), version, null, [], []);
    }

    private static InstallPlan CreatePlan(Architecture host, IReadOnlyList<Architecture> targets)
    {
        return new InstallPlan(
            "2026",
            "18.5.0",
            "14.50",
            "10.0.26100",
            "14.50",
            host.Cli(),
            targets.Select(x => x.Cli()).ToList(),
            [],
            [],
            [],
            [],
            [],
            []);
    }
}
