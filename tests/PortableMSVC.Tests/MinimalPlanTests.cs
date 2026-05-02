namespace PortableMSVC.Tests;

[TestClass]
public sealed class MinimalPlanTests
{
    [TestMethod]
    public void SelectsOnlyRequestedHostTargetToolPackages()
    {
        var plan = Build("2022", "14.44", "26100", "14.44", Architecture.X64, [Architecture.X64, Architecture.X86]);
        var roots = plan.RootPackages.Select(x => x.Id).ToList();

        Assert.IsTrue(plan.Success, string.Join(Environment.NewLine, plan.Issues.Select(x => x.Message)));
        CollectionAssert.Contains(roots, "Microsoft.VC.14.44.17.14.Tools.HostX64.TargetX64.base");
        CollectionAssert.Contains(roots, "Microsoft.VC.14.44.17.14.Tools.HostX64.TargetX86.base");
        CollectionAssert.DoesNotContain(roots, "Microsoft.VC.14.44.17.14.Tools.HostX86.TargetX64.base");
        CollectionAssert.DoesNotContain(roots, "Microsoft.VC.14.44.17.14.Tools.HostX86.TargetX86.base");
    }

    [TestMethod]
    public void IncludesRedistAndOfficialVcvarsButExcludesSetupStateRoots()
    {
        var plan = Build("2022", "14.44", "26100", "14.44", Architecture.X64, [Architecture.X64]);
        var roots = plan.RootPackages.Select(x => x.Id).ToList();

        Assert.IsTrue(plan.Success, string.Join(Environment.NewLine, plan.Issues.Select(x => x.Message)));
        CollectionAssert.Contains(roots, "Microsoft.VC.14.44.17.14.CRT.Redist.X64.base");
        CollectionAssert.Contains(roots, "Microsoft.VisualCpp.Tools.Core.x86");
        CollectionAssert.Contains(roots, "Microsoft.VisualCpp.Tools.HostX64.TargetX64");
        CollectionAssert.Contains(roots, "Microsoft.VisualStudio.VC.DevCmd");
        CollectionAssert.Contains(roots, "Microsoft.VisualStudio.VC.vcvars");
        CollectionAssert.Contains(roots, "Microsoft.VisualStudio.VsDevCmd.Core.WinSdk");
        CollectionAssert.DoesNotContain(roots, "Microsoft.VisualStudio.Setup.Configuration");
        CollectionAssert.DoesNotContain(roots, "Microsoft.VisualStudio.Setup.Configuration.Interop");
        CollectionAssert.DoesNotContain(roots, "Microsoft.VisualStudio.VsDevCmd.Core.DotNet");
        CollectionAssert.DoesNotContain(roots, "Microsoft.VisualStudio.VsDevCmd.Ext.NetFxSdk");
        CollectionAssert.Contains(roots, "Microsoft.VC.14.44.17.14.Premium.Tools.HostX64.TargetX64.base");
        CollectionAssert.Contains(roots, "Microsoft.VC.14.44.17.14.ATL.Headers.base");
        CollectionAssert.Contains(roots, "Microsoft.VC.14.44.17.14.ATL.Source.base");
        CollectionAssert.Contains(roots, "Microsoft.VC.14.44.17.14.ATL.X64.base");
    }

    [TestMethod]
    public void SelectsRequiredSdkPayloadsForRequestedTargets()
    {
        var plan = Build("2022", "14.44", "26100", "14.44", Architecture.X64, [Architecture.X64, Architecture.X86]);

        Assert.IsTrue(plan.Success, string.Join(Environment.NewLine, plan.Issues.Select(x => x.Message)));
        CollectionAssert.Contains(plan.SdkPayloads.ToList(), @"Installers\Windows SDK Desktop Libs x64-x86_en-us.msi");
        CollectionAssert.Contains(plan.SdkPayloads.ToList(), @"Installers\Windows SDK Desktop Libs x86-x86_en-us.msi");
        CollectionAssert.Contains(plan.SdkPayloads.ToList(), @"Installers\Universal CRT Headers Libraries and Sources-x86_en-us.msi");
        CollectionAssert.Contains(plan.SdkPayloads.ToList(), @"Installers\Universal CRT Redistributable-x86_en-us.msi");
    }

    [TestMethod]
    public void SelectsRuntimeInstallersForRequestedTargets()
    {
        var plan = Build("2026", "14.50", "26100", "14.50", Architecture.X64, [Architecture.X64, Architecture.X86, Architecture.Arm64], withRuntime: true);
        var payloads = plan.RuntimePayloads.Select(x => x.FileName).ToList();

        Assert.IsTrue(plan.Success, string.Join(Environment.NewLine, plan.Issues.Select(x => x.Message)));
        CollectionAssert.Contains(payloads, Path.Combine("Runtime", "x64", "VC_redist.x64.exe"));
        CollectionAssert.Contains(payloads, Path.Combine("Runtime", "x64", "vc_RuntimeDebug.msi"));
        CollectionAssert.Contains(payloads, Path.Combine("Runtime", "x64", "cab1.cab"));
        CollectionAssert.Contains(payloads, Path.Combine("Runtime", "x86", "VC_redist.x86.exe"));
        CollectionAssert.Contains(payloads, Path.Combine("Runtime", "x86", "vc_RuntimeDebug.msi"));
        CollectionAssert.Contains(payloads, Path.Combine("Runtime", "x86", "cab1.cab"));
        CollectionAssert.Contains(payloads, Path.Combine("Runtime", "arm64", "VC_redist.arm64.exe"));
        CollectionAssert.Contains(payloads, Path.Combine("Runtime", "arm64", "vc_RuntimeDebug_arm64.msi"));
        CollectionAssert.Contains(payloads, Path.Combine("Runtime", "arm64", "cab1.cab"));

        Assert.AreEqual(
            "3425a8f2b9a42ffe643c0ffe2d508cb46620becf9e43a0086bbb9fb1897f9626",
            plan.RuntimePayloads.Single(x => x.FileName == Path.Combine("Runtime", "x64", "vc_RuntimeDebug.msi")).Sha256);
        Assert.AreEqual(
            "45cd13328a00acc36daf2786c53583c02869974a87d00386f7fcbe30497bd50d",
            plan.RuntimePayloads.Single(x => x.FileName == Path.Combine("Runtime", "x64", "cab1.cab")).Sha256);
        Assert.AreEqual(
            "6af08b46bfc541e9025d96edda10fab9a3d84e95bd51fe402666b3bcd4c670a5",
            plan.RuntimePayloads.Single(x => x.FileName == Path.Combine("Runtime", "x86", "vc_RuntimeDebug.msi")).Sha256);
        Assert.AreEqual(
            "6f93417e93f3071425e013949de35fe910be0c0b67dad50612492d413696e514",
            plan.RuntimePayloads.Single(x => x.FileName == Path.Combine("Runtime", "x86", "cab1.cab")).Sha256);
    }

    [TestMethod]
    public void RuntimeInstallersAreOptIn()
    {
        var plan = Build("2026", "14.50", "26100", "14.50", Architecture.X64, [Architecture.X64, Architecture.X86, Architecture.Arm64]);

        Assert.IsTrue(plan.Success, string.Join(Environment.NewLine, plan.Issues.Select(x => x.Message)));
        Assert.IsEmpty(plan.RuntimePayloads);
    }

    [TestMethod]
    public void DependencyClosureFiltersMsvcPackagesForUnrequestedArchitectures()
    {
        var index = SyntheticMsvc1451Index();

        var plan = new PlanBuilder().Build(index, new PlanRequest(
            "test",
            "14.51",
            "26100",
            "14.51",
            Architecture.X64,
            [Architecture.X64, Architecture.X86],
            WithRuntime: false));

        Assert.IsTrue(plan.Success, string.Join(Environment.NewLine, plan.Issues.Select(x => $"{x.Code}: {x.Message}")));
        Assert.IsTrue(plan.DependencyClosure.Any(x => x.Id.Equals("Microsoft.VC.14.51.ASAN.X86.base", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(plan.DependencyClosure.Any(x => x.Id.Equals("Microsoft.VC.14.51.ASAN.X64.base", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(
            plan.DependencyClosure.Any(x => x.Id.Equals("Microsoft.VC.14.51.ASAN.ARM64.base", StringComparison.OrdinalIgnoreCase)),
            "Unrequested ARM64 ASAN package should not be downloaded just because x86 ASAN depends on it.");
        Assert.IsFalse(
            plan.Payloads.Any(x => x.PackageId.Equals("Microsoft.VC.14.51.ASAN.ARM64.base", StringComparison.OrdinalIgnoreCase)),
            "Unrequested ARM64 ASAN payload should not be downloaded.");
    }

    [TestMethod]
    public void Vs2019Arm32RuntimeInstallerAbsenceIsReportedFromManifest()
    {
        var plan = Build("2019", "14.29.16.11", "22621", "14.29.16.11", Architecture.X64, [Architecture.Arm], withRuntime: true);

        Assert.IsTrue(plan.Success, string.Join(Environment.NewLine, plan.Issues.Select(x => x.Message)));
        Assert.IsEmpty(plan.RuntimePayloads);
        Assert.IsTrue(
            plan.Issues.Any(x => x.Code == "missing-runtime-installer" && x.Message.Contains("arm", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, plan.Issues.Select(x => $"{x.Code}: {x.Message}")));
        Assert.IsTrue(
            plan.Issues.Any(x => x.Code == "missing-runtime-debug-installer" && x.Message.Contains("arm", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, plan.Issues.Select(x => $"{x.Code}: {x.Message}")));
    }

    [TestMethod]
    public void ReportsArm32UnsupportedForVs2026Msvc1450()
    {
        var plan = Build("2026", "14.50", "26100", "14.50", Architecture.X64, [Architecture.Arm]);

        Assert.IsFalse(plan.Success);
        Assert.IsTrue(plan.Issues.Any(x => x.Code == "missing-compiler-tools" && x.Suggestion is not null), string.Join(Environment.NewLine, plan.Issues.Select(x => x.Message)));
        Assert.IsFalse(plan.Issues.Any(x => x.Code == "missing-vcvars-host-target"), string.Join(Environment.NewLine, plan.Issues.Select(x => $"{x.Code}: {x.Message}")));
        Assert.IsFalse(plan.Issues.Any(x => x.Code == "missing-sdk-payload"), string.Join(Environment.NewLine, plan.Issues.Select(x => $"{x.Code}: {x.Message}")));
        Assert.IsFalse(plan.Issues.Any(x => x.Code == "missing-redist"), string.Join(Environment.NewLine, plan.Issues.Select(x => $"{x.Code}: {x.Message}")));
        Assert.IsEmpty(plan.RootPackages);
        Assert.IsEmpty(plan.Payloads);
    }

    [TestMethod]
    public void ReportsArm32HostAsUnsupportedArchitecture()
    {
        var plan = Build("2019", "14.29.16.11", "10.0.22621", "14.29.16.11", Architecture.Arm, [Architecture.Arm64]);

        Assert.IsFalse(plan.Success);
        Assert.IsTrue(plan.Issues.Any(x => x.Code == "unsupported-host-architecture"), string.Join(Environment.NewLine, plan.Issues.Select(x => $"{x.Code}: {x.Message}")));
        Assert.IsFalse(plan.Issues.Any(x => x.Message.Contains("HostARM", StringComparison.OrdinalIgnoreCase)), string.Join(Environment.NewLine, plan.Issues.Select(x => $"{x.Code}: {x.Message}")));
    }

    [TestMethod]
    public void ReportsMissingOfficialVcvarsHostTargetPackage()
    {
        var index = WithoutPackage(ManifestTestData.Load("2022"), "Microsoft.VisualCpp.Tools.HostARM64.TargetX86");
        var plan = new PlanBuilder().Build(index, new PlanRequest(
            "2022",
            "14.44",
            "26100",
            "14.44",
            Architecture.Arm64,
            [Architecture.X86],
            WithRuntime: false));

        Assert.IsFalse(plan.Success);
        Assert.IsTrue(
            plan.Issues.Any(x => x.Code == "missing-vcvars-host-target"
                && x.Message.Contains("主机架构 arm64 到目标架构 x86", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, plan.Issues.Select(x => $"{x.Code}: {x.Message}")));
        Assert.IsFalse(plan.Issues.Any(x => x.Code == "missing-package"), string.Join(Environment.NewLine, plan.Issues.Select(x => $"{x.Code}: {x.Message}")));
    }

    [TestMethod]
    public void ReportsMissingCompilerToolsHostTargetPackage()
    {
        var index = WithoutPackage(ManifestTestData.Load("2022"), "Microsoft.VC.14.44.17.14.Tools.HostARM64.TargetX86.base");
        var plan = new PlanBuilder().Build(index, new PlanRequest(
            "2022",
            "14.44",
            "26100",
            "14.44",
            Architecture.Arm64,
            [Architecture.X86],
            WithRuntime: false));

        Assert.IsFalse(plan.Success);
        Assert.IsTrue(
            plan.Issues.Any(x => x.Code == "missing-compiler-tools"
                && x.Message.Contains("主机架构 arm64 到目标架构 x86", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, plan.Issues.Select(x => $"{x.Code}: {x.Message}")));
        Assert.IsFalse(plan.Issues.Any(x => x.Code == "missing-vcvars-host-target"), string.Join(Environment.NewLine, plan.Issues.Select(x => $"{x.Code}: {x.Message}")));
        Assert.IsFalse(plan.Issues.Any(x => x.Code == "missing-package"), string.Join(Environment.NewLine, plan.Issues.Select(x => $"{x.Code}: {x.Message}")));
    }

    [TestMethod]
    public void SuggestsAvailableHostsWhenSelectedMsvcDoesNotSupportArm64Host()
    {
        var plan = Build("2022", "14.29.16.11", "10.0.22621", "14.29.16.11", Architecture.Arm64, [Architecture.Arm64]);

        Assert.IsFalse(plan.Success);
        var issue = plan.Issues.Single(x => x.Code == "missing-compiler-tools");
        StringAssert.Contains(issue.Suggestion, "可用主机架构只有 x86、x64");
    }

    [TestMethod]
    [DataRow("2019", "14.29", "19041")]
    [DataRow("2022", "14.44", "26100")]
    [DataRow("2026", "14.50", "26100")]
    [DataRow("latest", "14.50", "26100")]
    public void BuildsMinimalPlanForKnownChannels(string vs, string vc, string sdk)
    {
        var plan = Build(vs, vc, sdk, vc, Architecture.X64, [Architecture.X64]);

        Assert.IsTrue(plan.Success, string.Join(Environment.NewLine, plan.Issues.Select(x => x.Message)));
        Assert.AreNotEqual(0, plan.RootPackages.Count);
        Assert.AreNotEqual(0, plan.Payloads.Count);
        Assert.AreNotEqual(0, plan.SdkPayloads.Count);
    }

    private static InstallPlan Build(
        string vs,
        string vc,
        string sdk,
        string redist,
        Architecture host,
        IReadOnlyList<Architecture> targets,
        bool withRuntime = false)
    {
        var index = ManifestTestData.Load(vs);
        return new PlanBuilder().Build(index, new PlanRequest(vs, vc, sdk, redist, host, targets, withRuntime));
    }

    private static PackageIndex WithoutPackage(PackageIndex index, string id)
    {
        var packages = index.Packages
            .Where(x => !x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new PackageIndex(
            index.VsAlias,
            index.ProductVersion,
            index.BuildVersion,
            index.ProductSemanticVersion,
            index.ProductLine,
            index.ProductLineVersion,
            index.ProductInfoId,
            packages);
    }

    private static PackageIndex SyntheticMsvc1451Index()
    {
        List<PackageInfo> packages =
        [
            Package("Microsoft.VisualCpp.DIA.SDK"),
            Package("Microsoft.VisualCpp.Tools.Core.x86"),
            Package("Microsoft.VisualCpp.Tools.HostX64.TargetX64"),
            Package("Microsoft.VisualCpp.Tools.HostX64.TargetX86"),
            Package("Microsoft.VisualStudio.VC.DevCmd"),
            Package("Microsoft.VisualStudio.VC.vcvars"),
            Package("Microsoft.VisualStudio.VsDevCmd.Core.WinSdk"),
            Package("Microsoft.VC.14.51.CRT.Headers.base"),
            Package("Microsoft.VC.14.51.CRT.Source.base"),
            Package("Microsoft.VC.14.51.ASAN.Headers.base"),
            Package("Microsoft.VC.14.51.PGO.Headers.base"),
            Package("Microsoft.VC.14.51.ATL.Headers.base"),
            Package("Microsoft.VC.14.51.ATL.Source.base"),
            Package("Microsoft.VC.14.51.Tools.HostX64.TargetX64.base", payloads: [Payload("tools-x64.vsix")]),
            Package("Microsoft.VC.14.51.Tools.HostX64.TargetX86.base", payloads: [Payload("tools-x86.vsix")]),
            Package("Microsoft.VC.14.51.Tools.HostX64.TargetX64.Res.base"),
            Package("Microsoft.VC.14.51.Tools.HostX64.TargetX86.Res.base"),
            Package("Microsoft.VC.14.51.CRT.x64.Desktop.base"),
            Package("Microsoft.VC.14.51.CRT.x64.Store.base"),
            Package("Microsoft.VC.14.51.CRT.x86.Desktop.base"),
            Package("Microsoft.VC.14.51.CRT.x86.Store.base"),
            Package("Microsoft.VC.14.51.Premium.Tools.HostX64.TargetX64.base"),
            Package("Microsoft.VC.14.51.Premium.Tools.HostX64.TargetX86.base"),
            Package("Microsoft.VC.14.51.PGO.x64.base"),
            Package("Microsoft.VC.14.51.PGO.x86.base"),
            Package("Microsoft.VC.14.51.ATL.x64.base"),
            Package("Microsoft.VC.14.51.ATL.x86.base"),
            Package("Microsoft.VC.14.51.CRT.Redist.x64.base"),
            Package("Microsoft.VC.14.51.CRT.Redist.x86.base"),
            Package("Microsoft.VC.14.51.ASAN.X64.base", payloads: [Payload("asan-x64.vsix")]),
            Package(
                "Microsoft.VC.14.51.ASAN.X86.base",
                [
                    "Microsoft.VC.14.51.ASAN.Headers.base",
                    "Microsoft.VC.14.51.ASAN.X64.base",
                    "Microsoft.VC.14.51.ASAN.ARM64.base"
                ],
                [Payload("asan-x86.vsix")]),
            Package("Microsoft.VC.14.51.ASAN.ARM64.base", payloads: [Payload("asan-arm64.vsix")]),
            Package("Win11SDK_10.0.26100", ["Win11SDKPayloads"]),
            Package(
                "Win11SDKPayloads",
                payloads:
                [
                    Payload(@"Installers\Windows SDK for Windows Store Apps Tools-x86_en-us.msi"),
                    Payload(@"Installers\Windows SDK for Windows Store Apps Headers-x86_en-us.msi"),
                    Payload(@"Installers\Windows SDK for Windows Store Apps Libs-x86_en-us.msi"),
                    Payload(@"Installers\Universal CRT Headers Libraries and Sources-x86_en-us.msi"),
                    Payload(@"Installers\Universal CRT Redistributable-x86_en-us.msi"),
                    Payload(@"Installers\Windows SDK Desktop Libs x64-x86_en-us.msi"),
                    Payload(@"Installers\Windows SDK Desktop Libs x86-x86_en-us.msi")
                ]),
        ];

        return new PackageIndex("test", "test", "test", "test", "DevTest", "0", "VisualStudio", packages);
    }

    private static PackageInfo Package(string id, IReadOnlyList<string>? dependencies = null, IReadOnlyList<PayloadInfo>? payloads = null)
    {
        return new PackageInfo(id, id.ToLowerInvariant(), null, null, dependencies ?? [], payloads ?? []);
    }

    private static PayloadInfo Payload(string fileName)
    {
        return new PayloadInfo(fileName, "https://example.invalid/" + fileName.Replace('\\', '/'), "sha256", 1);
    }
}
