namespace PortableMSVC.Tests;

[TestClass]
public sealed class VersionCatalogTests
{
    [TestMethod]
    public void FindsKnownVersionsFromVs2022Manifest()
    {
        var index = ManifestTestData.Load("2022");

        CollectionAssert.Contains(index.GetMsvcVersions().ToList(), "14.44.17.14");
        CollectionAssert.Contains(index.GetSdkVersions().ToList(), "10.0.26100");
        CollectionAssert.Contains(index.GetRedistVersions().ToList(), "14.44.17.14");
    }

    [TestMethod]
    public void ShortVersionAliasResolvesToFullMsvcVersion()
    {
        var index = ManifestTestData.Load("2022");

        var version = index.ResolveMsvcVersion("14.44");

        Assert.AreEqual("14.44.17.14", version.FullVersion);
    }

    [TestMethod]
    public void SdkShortAliasResolvesToFullSdkVersion()
    {
        var index = ManifestTestData.Load("2022");

        var version = index.ResolveSdkVersion("26100");

        Assert.AreEqual("10.0.26100", version.FullVersion);
    }

    [TestMethod]
    public void SupportsPreviewLikeTwoSegmentMsvcPackageVersion()
    {
        var index = ManifestTestData.Load("2026");

        CollectionAssert.Contains(index.GetMsvcVersions().ToList(), "14.51");
    }
}
