using System.Reflection;

namespace PortableMSVC.Tests;

[TestClass]
public sealed class RuntimeDllCopyTests
{
    [TestMethod]
    public void CleanupWindowsKitBinKeepsHostToolsOnly()
    {
        string root = Path.Combine(Path.GetTempPath(), "PortableMSVC.Tests.Work", Guid.NewGuid().ToString("N"));
        try
        {
            string kit = Path.Combine(root, "Windows Kits", "10");
            WriteFile(kit, @"bin\10.0.26100.0\x64\rc.exe");
            WriteFile(kit, @"bin\10.0.26100.0\x86\rc.exe");
            WriteFile(kit, @"bin\10.0.26100.0\x86\ucrt\ucrtbased.dll");
            WriteFile(kit, @"bin\10.0.26100.0\arm64\rc.exe");

            var plan = TestPlan(host: "x64", targets: ["x86"]);

            InvokePrivateStatic("CleanupWindowsKitBin", kit, plan);

            Assert.IsTrue(File.Exists(Path.Combine(kit, @"bin\10.0.26100.0\x64\rc.exe")));
            Assert.IsFalse(File.Exists(Path.Combine(kit, @"bin\10.0.26100.0\x86\rc.exe")));
            Assert.IsFalse(Directory.Exists(Path.Combine(kit, @"bin\10.0.26100.0\x86")));
            Assert.IsFalse(Directory.Exists(Path.Combine(kit, @"bin\10.0.26100.0\arm64")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void CopyUcrtDebugRuntimeDllsCopiesTargetsToRedist()
    {
        string root = Path.Combine(Path.GetTempPath(), "PortableMSVC.Tests.Work", Guid.NewGuid().ToString("N"));
        try
        {
            WriteFile(root, @"Windows Kits\10\bin\10.0.26100.0\x64\ucrt\ucrtbased.dll");
            WriteFile(root, @"Windows Kits\10\bin\10.0.26100.0\x86\ucrt\ucrtbased.dll");
            WriteFile(root, @"Windows Kits\10\bin\10.0.26100.0\arm64\ucrt\ucrtbased.dll");
            Directory.CreateDirectory(Path.Combine(root, @"Windows Kits\10\Redist\10.0.26100.0\ucrt\DLLs"));

            var plan = TestPlan(host: "x64", targets: ["x86", "arm64"]);

            InvokePrivateStatic("CopyUcrtDebugRuntimeDlls", root, plan);

            Assert.IsTrue(File.Exists(Path.Combine(root, @"Windows Kits\10\Redist\10.0.26100.0\ucrt\DLLs\x86\ucrtbased.dll")));
            Assert.IsTrue(File.Exists(Path.Combine(root, @"Windows Kits\10\Redist\10.0.26100.0\ucrt\DLLs\arm64\ucrtbased.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(root, @"Windows Kits\10\Redist\10.0.26100.0\ucrt\DLLs\x64\ucrtbased.dll")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void CopyRuntimeDllsUsesTargetAndUcrtWhitelist()
    {
        string root = Path.Combine(Path.GetTempPath(), "PortableMSVC.Tests.Work", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "BuildTools", "VC", "Tools", "MSVC", "14.50.35717"));
            WriteFile(root, @"BuildTools\VC\Redist\MSVC\14.50.35710\x64\Microsoft.VC145.CRT\vcruntime140.dll");
            WriteFile(root, @"BuildTools\VC\Redist\MSVC\14.50.35710\debug_nonredist\x64\Microsoft.VC145.DebugCRT\vcruntime140d.dll");
            WriteFile(root, @"BuildTools\VC\Redist\MSVC\14.50.35710\x86\Microsoft.VC145.CRT\vcruntime140.dll");
            WriteFile(root, @"Windows Kits\10\Redist\10.0.26100.0\ucrt\DLLs\x64\ucrtbase.dll");
            WriteFile(root, @"Windows Kits\10\Redist\10.0.26100.0\ucrt\DLLs\x64\ucrtbased.dll");
            WriteFile(root, @"Windows Kits\10\Redist\10.0.26100.0\ucrt\DLLs\x64\api-ms-win-core-test.dll");
            WriteFile(root, @"Windows Kits\10\Redist\D3D\x64\d3dcompiler_47.dll");
            WriteFile(root, @"Windows Kits\10\Redist\MBN\x64\microsoft.mbn.dll");

            var plan = TestPlan(host: "x64", targets: ["x64"]);

            InvokeCopyRuntimeDlls(root, plan);

            string destination = Path.Combine(root, "BuildTools", "VC", "Tools", "MSVC", "14.50.35717", "bin", "Hostx64", "x64");
            Assert.IsTrue(File.Exists(Path.Combine(destination, "vcruntime140.dll")));
            Assert.IsTrue(File.Exists(Path.Combine(destination, "vcruntime140d.dll")));
            Assert.IsTrue(File.Exists(Path.Combine(destination, "ucrtbase.dll")));
            Assert.IsTrue(File.Exists(Path.Combine(destination, "ucrtbased.dll")));
            Assert.IsTrue(File.Exists(Path.Combine(destination, "d3dcompiler_47.dll")));
            Assert.IsTrue(File.Exists(Path.Combine(destination, "microsoft.mbn.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(destination, "api-ms-win-core-test.dll")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void InvokeCopyRuntimeDlls(string root, InstallPlan plan)
    {
        InvokePrivateStatic("CopyRuntimeDlls", root, plan);
    }

    private static object? InvokePrivateStatic(string methodName, params object?[] args)
    {
        var method = typeof(InstallRunner).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(InstallRunner), methodName);
        return method.Invoke(null, args);
    }

    private static InstallPlan TestPlan(string host, IReadOnlyList<string> targets)
    {
        return new InstallPlan(
            "2022",
            "17.14",
            "14.50.18.0",
            "10.0.26100",
            "14.50.18.0",
            host,
            targets,
            [],
            [],
            [],
            [],
            [],
            []);
    }

    private static void WriteFile(string root, string relativePath)
    {
        string path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? root);
        File.WriteAllText(path, "test");
    }
}
