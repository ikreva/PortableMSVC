namespace PortableMSVC.Tests;

[TestClass]
public sealed class PackageSelectionMatrixTests
{
    private static readonly string[] VsAliases = ["2019", "2022", "2026", "latest"];

    private static readonly Architecture[] DesktopHosts =
    [
        Architecture.X64,
        Architecture.X86,
        Architecture.Arm64,
    ];

    private static readonly Architecture[] Sdk26100Targets =
    [
        Architecture.X64,
        Architecture.X86,
        Architecture.Arm64,
    ];

    [TestMethod]
    public void VersionCatalogsMatchToolAndSdkPackagesInManifests()
    {
        foreach (var vs in VsAliases)
        {
            var index = ManifestTestData.Load(vs);

            CollectionAssert.AreEqual(
                DiscoverMsvcToolVersions(index).ToList(),
                index.GetMsvcVersions().ToList(),
                $"MSVC versions differ for VS {vs}.");

            CollectionAssert.AreEqual(
                DiscoverSdkVersions(index).ToList(),
                index.GetSdkVersions().ToList(),
                $"SDK versions differ for VS {vs}.");

            CollectionAssert.AreEqual(
                DiscoverRedistVersions(index).ToList(),
                index.GetRedistVersions().ToList(),
                $"redist versions differ for VS {vs}.");
        }
    }

    [TestMethod]
    public void ShortAndFullMsvcAliasesResolveAcrossAllCatalogs()
    {
        foreach (var vs in VsAliases)
        {
            var index = ManifestTestData.Load(vs);

            foreach (var fullVersion in index.GetMsvcVersions())
            {
                Assert.AreEqual(fullVersion, index.ResolveMsvcVersion(fullVersion).FullVersion);

                var shortVersion = ToMsvcShortVersion(fullVersion);
                var expected = index.GetMsvcVersions()
                    .Where(x => x.Equals(shortVersion, StringComparison.OrdinalIgnoreCase)
                        || x.StartsWith(shortVersion + ".", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x, NaturalVersionComparer.Instance)
                    .Last();

                Assert.AreEqual(expected, index.ResolveMsvcVersion(shortVersion).FullVersion);
            }
        }
    }

    [TestMethod]
    public void RedistDefaultsToLatestVersionNotNewerThanSelectedMsvc()
    {
        foreach (var vs in VsAliases)
        {
            var index = ManifestTestData.Load(vs);
            var redistVersions = index.GetRedistVersions();

            foreach (var fullVersion in index.GetMsvcVersions())
            {
                var expected = redistVersions
                    .Where(x => NaturalVersionComparer.Instance.Compare(x, fullVersion) <= 0)
                    .LastOrDefault();

                Assert.IsNotNull(expected, $"Test manifest has no redist not newer than VS {vs}, MSVC {fullVersion}.");

                var actual = index.ResolveRedistVersion(null, fullVersion).FullVersion;

                Assert.AreEqual(expected, actual, $"Unexpected redist for VS {vs}, MSVC {fullVersion}.");
            }
        }
    }

    [TestMethod]
    public void RedistDefaultFailsWhenOnlyNewerRedistVersionsExist()
    {
        var index = new PackageIndex(
            "test",
            "test",
            "test",
            "test",
            "DevTest",
            "0",
            "VisualStudio",
            [
                Package("Microsoft.VC.14.1.Tools.HostX64.TargetX64.base"),
                Package("Microsoft.VC.14.2.CRT.Redist.X64.base"),
            ]);

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => index.ResolveRedistVersion(null, "14.1"));

        StringAssert.Contains(ex.Message, "没有找到不高于 MSVC 14.1 的 redist 版本");
    }

    [TestMethod]
    public void PlanRedistDefaultUsesResolvedMsvcVersionWhenVcIsImplicit()
    {
        foreach (var vs in VsAliases)
        {
            var index = ManifestTestData.Load(vs);
            var selectedMsvc = index.ResolveMsvcVersion(null).FullVersion;
            var expectedRedist = index.ResolveRedistVersion(null, selectedMsvc).FullVersion;
            var sdk = index.GetSdkVersions()[^1].Split('.')[^1];

            var plan = new PlanBuilder().Build(index, new PlanRequest(
                vs,
                null,
                sdk,
                null,
                Architecture.X64,
                [Architecture.X64],
                WithRuntime: false));

            Assert.IsTrue(plan.Success, FailureMessage(vs, "implicit-vc-redist", plan));
            Assert.AreEqual(expectedRedist, plan.RedistVersion, $"Unexpected default redist for VS {vs}.");
        }
    }

    [TestMethod]
    public void BuildsX64MinimalPlanForEveryDiscoveredMsvcVersion()
    {
        foreach (var vs in VsAliases)
        {
            var index = ManifestTestData.Load(vs);
            var sdk = index.GetSdkVersions()[^1].Split('.')[^1];

            foreach (var fullVersion in index.GetMsvcVersions())
            {
                var plan = new PlanBuilder().Build(index, new PlanRequest(
                    vs,
                    fullVersion,
                    sdk,
                    null,
                    Architecture.X64,
                    [Architecture.X64],
                    WithRuntime: false));

                Assert.IsTrue(plan.Success, FailureMessage(vs, fullVersion, plan));
                AssertContainsPackage(
                    plan.RootPackages,
                    $"Microsoft.VC.{fullVersion}.Tools.HostX64.TargetX64.base");
            }
        }
    }

    [TestMethod]
    public void PlanExcludesMsvcAuxiliaryPropsPackages()
    {
        var index = ManifestTestData.Load("2026");

        var plan = new PlanBuilder().Build(index, new PlanRequest(
            "2026",
            "14.50",
            "26100",
            null,
            Architecture.X64,
            [Architecture.X64],
            WithRuntime: false));

        Assert.IsTrue(plan.Success, FailureMessage("2026", "props-filter", plan));

        foreach (var package in plan.DependencyClosure)
        {
            Assert.IsFalse(
                IsMsvcAuxiliaryPropsPackage(package.Id),
                "Auxiliary props package should not be downloaded: " + package.Id);
        }

        foreach (var payload in plan.Payloads)
        {
            Assert.IsFalse(
                IsMsvcAuxiliaryPropsPackage(payload.PackageId),
                "Auxiliary props payload should not be downloaded: " + payload.PackageId);
        }
    }

    [TestMethod]
    public void LatestVs2026PlanDoesNotPullOlderMsvcToolsetFromVcvarsWrappers()
    {
        var index = ManifestTestData.Load("2026");

        var plan = new PlanBuilder().Build(index, new PlanRequest(
            "2026",
            null,
            "26100",
            null,
            Architecture.X64,
            [Architecture.X64, Architecture.X86, Architecture.Arm64],
            WithRuntime: false));

        Assert.IsTrue(plan.Success, FailureMessage("2026", "latest-toolset", plan));
        Assert.AreEqual("14.51", plan.MsvcVersion);

        foreach (var package in plan.DependencyClosure)
        {
            Assert.IsFalse(
                package.Id.StartsWith("Microsoft.VC.14.50.", StringComparison.OrdinalIgnoreCase),
                "Older MSVC package should not be downloaded: " + package.Id);
        }

        foreach (var payload in plan.Payloads)
        {
            Assert.IsFalse(
                payload.PackageId.StartsWith("Microsoft.VC.14.50.", StringComparison.OrdinalIgnoreCase)
                    || payload.FileName.StartsWith("Microsoft.VC.14.50.", StringComparison.OrdinalIgnoreCase),
                "Older MSVC payload should not be downloaded: " + payload.FileName);
        }
    }

    [TestMethod]
    public void BuildsVs2022Sdk26100HostTargetMatrixWithoutExtraToolCombinations()
    {
        var index = ManifestTestData.Load("2022");

        foreach (var host in DesktopHosts)
        {
            foreach (var target in Sdk26100Targets)
            {
                var plan = new PlanBuilder().Build(index, new PlanRequest(
                    "2022",
                    "14.44",
                    "26100",
                    null,
                    host,
                    [target],
                    WithRuntime: false));

                Assert.IsTrue(plan.Success, FailureMessage("2022", $"{host.Cli()}->{target.Cli()}", plan));

                AssertContainsPackage(
                    plan.RootPackages,
                    $"Microsoft.VC.14.44.17.14.Tools.Host{host.PackageTitle()}.Target{target.PackageTitle()}.base");

                foreach (var otherHost in DesktopHosts.Where(x => x != host))
                {
                    AssertDoesNotContainPackage(
                        plan.RootPackages,
                        $"Microsoft.VC.14.44.17.14.Tools.Host{otherHost.PackageTitle()}.Target{target.PackageTitle()}.base");
                }
            }
        }
    }

    [TestMethod]
    public void SdkPayloadSelectionTracks22621And26100Shape()
    {
        var index = ManifestTestData.Load("2022");

        var sdk22621Plan = new PlanBuilder().Build(index, new PlanRequest(
            "2022",
            "14.44",
            "22621",
            null,
            Architecture.X64,
            [Architecture.Arm],
            WithRuntime: false));

        Assert.IsTrue(sdk22621Plan.Success, FailureMessage("2022", "sdk22621-arm", sdk22621Plan));
        CollectionAssert.Contains(
            sdk22621Plan.SdkPayloads.ToList(),
            @"Installers\Windows SDK Desktop Headers arm-x86_en-us.msi");
        CollectionAssert.Contains(
            sdk22621Plan.SdkPayloads.ToList(),
            @"Installers\Windows SDK Desktop Libs arm-x86_en-us.msi");
        CollectionAssert.DoesNotContain(
            sdk22621Plan.SdkPayloads.ToList(),
            @"Installers\Windows SDK OnecoreUap Headers arm-x86_en-us.msi");

        var sdk26100Plan = new PlanBuilder().Build(index, new PlanRequest(
            "2022",
            "14.44",
            "26100",
            null,
            Architecture.X64,
            [Architecture.Arm64],
            WithRuntime: false));

        Assert.IsTrue(sdk26100Plan.Success, FailureMessage("2022", "sdk26100-arm64", sdk26100Plan));
        CollectionAssert.DoesNotContain(
            sdk26100Plan.SdkPayloads.ToList(),
            @"Installers\Windows SDK Desktop Headers arm-x86_en-us.msi");
        CollectionAssert.Contains(
            sdk26100Plan.SdkPayloads.ToList(),
            @"Installers\Windows SDK OnecoreUap Headers arm64-x86_en-us.msi");
    }

    private static IEnumerable<string> DiscoverMsvcToolVersions(PackageIndex index)
    {
        return index.Packages
            .Select(x => x.Id)
            .Where(x => x.StartsWith("Microsoft.VC.", StringComparison.OrdinalIgnoreCase)
                && x.EndsWith(".Tools.HostX64.TargetX64.base", StringComparison.OrdinalIgnoreCase)
                && !x.Contains("Preview", StringComparison.OrdinalIgnoreCase)
                && !x.Contains("Premium", StringComparison.OrdinalIgnoreCase))
            .Select(x => x["Microsoft.VC.".Length..^".Tools.HostX64.TargetX64.base".Length])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, NaturalVersionComparer.Instance);
    }

    private static IEnumerable<string> DiscoverSdkVersions(PackageIndex index)
    {
        return index.Packages
            .Select(x => x.Id)
            .Where(x => x.StartsWith("Win10SDK_10.0.", StringComparison.OrdinalIgnoreCase)
                || x.StartsWith("Win11SDK_10.0.", StringComparison.OrdinalIgnoreCase))
            .Select(x => x[(x.IndexOf('_') + 1)..])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, NaturalVersionComparer.Instance);
    }

    private static IEnumerable<string> DiscoverRedistVersions(PackageIndex index)
    {
        return index.Packages
            .Select(x => x.Id)
            .Where(x => x.StartsWith("Microsoft.VC.", StringComparison.OrdinalIgnoreCase)
                && x.Contains(".CRT.Redist.", StringComparison.OrdinalIgnoreCase)
                && x.EndsWith(".base", StringComparison.OrdinalIgnoreCase))
            .Select(x => x["Microsoft.VC.".Length..x.IndexOf(".CRT.Redist.", StringComparison.OrdinalIgnoreCase)])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, NaturalVersionComparer.Instance);
    }

    private static string ToMsvcShortVersion(string fullVersion)
    {
        var parts = fullVersion.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : fullVersion;
    }

    private static bool IsMsvcAuxiliaryPropsPackage(string packageId)
    {
        if (packageId.StartsWith("Microsoft.VisualCpp.Servicing.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        const string prefix = "Microsoft.VC.";
        if (!packageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return packageId.IndexOf(".Props", prefix.Length, StringComparison.OrdinalIgnoreCase) >= 0
            || packageId.IndexOf(".Servicing", prefix.Length, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string FailureMessage(string vs, string scenario, InstallPlan plan)
    {
        return $"VS {vs}, scenario {scenario}:{Environment.NewLine}"
            + string.Join(Environment.NewLine, plan.Issues.Select(x => $"{x.Code}: {x.Message} {x.Suggestion}"));
    }

    private static void AssertContainsPackage(IEnumerable<PlannedPackage> packages, string expected)
    {
        Assert.IsTrue(
            packages.Any(x => x.Id.Equals(expected, StringComparison.OrdinalIgnoreCase)),
            $"Expected package '{expected}' was not selected.");
    }

    private static void AssertDoesNotContainPackage(IEnumerable<PlannedPackage> packages, string unexpected)
    {
        Assert.IsFalse(
            packages.Any(x => x.Id.Equals(unexpected, StringComparison.OrdinalIgnoreCase)),
            $"Unexpected package '{unexpected}' was selected.");
    }

    private static PackageInfo Package(string id)
    {
        return new PackageInfo(id, id.ToLowerInvariant(), null, null, [], []);
    }
}
