using System.Reflection;

namespace PortableMSVC.Tests;

[TestClass]
public sealed class MsiExtractorTests
{
    [TestMethod]
    public void SafeOutputPathRejectsSiblingDirectoryTraversal()
    {
        var method = typeof(MsiExtractor).GetMethod("SafeOutputPath", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(MsiExtractor), "SafeOutputPath");
        var root = Path.Combine(Path.GetTempPath(), "PortableMSVC.Tests.Work", Guid.NewGuid().ToString("N"), "out");

        var ex = Assert.ThrowsExactly<TargetInvocationException>(() => method.Invoke(null, [root, "..", "escaped.txt"]));

        Assert.IsInstanceOfType<InvalidDataException>(ex.InnerException);
    }
}
