using System.IO.Compression;

namespace PortableMSVC.Tests;

[TestClass]
public sealed class VsixExtractorTests
{
    [TestMethod]
    public void ExtractContentsRejectsSiblingDirectoryTraversal()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "PortableMSVC.Tests.Work", Guid.NewGuid().ToString("N"));
        var output = Path.Combine(tempRoot, "out");
        var vsix = Path.Combine(tempRoot, "test.vsix");
        Directory.CreateDirectory(tempRoot);

        try
        {
            using (var archive = ZipFile.Open(vsix, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("Contents/../out2/escaped.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("escaped");
            }

            Assert.ThrowsExactly<InvalidDataException>(() => VsixExtractor.ExtractContents(vsix, output));
            Assert.IsFalse(File.Exists(Path.Combine(tempRoot, "out2", "escaped.txt")));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
