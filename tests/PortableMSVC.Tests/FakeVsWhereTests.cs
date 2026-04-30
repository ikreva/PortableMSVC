using System.Diagnostics;
using System.Text.Json;

namespace PortableMSVC.Tests;

[TestClass]
[DoNotParallelize]
public sealed class FakeVsWhereTests
{
    [TestMethod]
    public void HelpCommandPrintsVsWhereHelp()
    {
        string installerDirectory = Path.Combine(Path.GetTempPath(), "PortableMSVC.Tests.Work", Guid.NewGuid().ToString("N"), "VisualStudio", "Installer");
        Directory.CreateDirectory(installerDirectory);
        try
        {
            var result = Run(installerDirectory, ["help"]);

            Assert.AreEqual(0, result.ExitCode, result.Error);
            StringAssert.Contains(result.Output, "Portable MSVC fake vswhere");
            StringAssert.Contains(result.Output, "用法：");
            StringAssert.Contains(result.Output, "-property <属性>");
            Assert.IsFalse(result.Output.TrimStart().StartsWith("[", StringComparison.Ordinal), result.Output);
            Assert.AreEqual("", result.Error);
        }
        finally
        {
            string root = Path.GetFullPath(Path.Combine(installerDirectory, "..", ".."));
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void SlashQuestionMarkPrintsVsWhereHelp()
    {
        using var layout = FakeVsWhereLayout.Create();

        var result = Run(layout.InstallerDirectory, ["/?"]);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        StringAssert.Contains(result.Output, "-h, --help, /?, help");
    }

    [TestMethod]
    public void PropertyInstallationPathSupportsIlCompilerQuery()
    {
        using var layout = FakeVsWhereLayout.Create();

        var result = Run(layout.InstallerDirectory, [
            "-latest",
            "-prerelease",
            "-products",
            "*",
            "-requires",
            "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
            "-property",
            "installationPath"]);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.AreEqual(layout.BuildToolsDirectory, result.Output.Trim());
    }

    [TestMethod]
    public void RelativeInstallationPathIsResolvedFromPortableRoot()
    {
        using var layout = FakeVsWhereLayout.Create();

        var result = Run(layout.InstallerDirectory, ["-property", "installationPath"]);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.AreEqual(layout.BuildToolsDirectory, result.Output.Trim());
    }

    [TestMethod]
    public void JunctionInstallerPathIsResolvedBackToPortableRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Junction resolution is Windows-specific.");
        }

        using var layout = FakeVsWhereLayout.Create();
        string junctionParent = Path.Combine(Path.GetTempPath(), "PortableMSVC.Tests.Work", "Junctions", Guid.NewGuid().ToString("N"));
        string junctionDirectory = Path.Combine(junctionParent, "Installer");
        Directory.CreateDirectory(junctionParent);
        try
        {
            if (!TryCreateJunction(junctionDirectory, layout.InstallerDirectory, out string error))
            {
                Assert.Inconclusive("Could not create test junction: " + error);
            }

            var result = Run(junctionDirectory, ["-property", "installationPath"]);

            Assert.AreEqual(0, result.ExitCode, result.Error);
            Assert.AreEqual(layout.BuildToolsDirectory, result.Output.Trim());
        }
        finally
        {
            if (Directory.Exists(junctionDirectory))
            {
                Directory.Delete(junctionDirectory);
            }

            if (Directory.Exists(junctionParent))
            {
                Directory.Delete(junctionParent, recursive: true);
            }
        }
    }

    [TestMethod]
    public void JsonWithoutFiltersSupportsCMakeQuery()
    {
        using var layout = FakeVsWhereLayout.Create();

        var result = Run(layout.InstallerDirectory, ["-format", "json"]);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        using var document = JsonDocument.Parse(result.Output);
        var instance = document.RootElement.EnumerateArray().Single();
        Assert.AreEqual("17.14.31.0", instance.GetProperty("installationVersion").GetString());
        Assert.AreEqual(layout.BuildToolsDirectory, instance.GetProperty("installationPath").GetString());
    }

    [TestMethod]
    public void JsonOutputUsesStableInstallDateFromState()
    {
        using var layout = FakeVsWhereLayout.Create();

        var first = Run(layout.InstallerDirectory, ["-format", "json"]);
        var second = Run(layout.InstallerDirectory, ["-format", "json"]);

        Assert.AreEqual(0, first.ExitCode, first.Error);
        Assert.AreEqual(0, second.ExitCode, second.Error);
        using var firstDocument = JsonDocument.Parse(first.Output);
        using var secondDocument = JsonDocument.Parse(second.Output);
        string? firstInstallDate = firstDocument.RootElement.EnumerateArray().Single().GetProperty("installDate").GetString();
        string? secondInstallDate = secondDocument.RootElement.EnumerateArray().Single().GetProperty("installDate").GetString();
        Assert.AreEqual("2026-04-29T00:00:00Z", firstInstallDate);
        Assert.AreEqual(firstInstallDate, secondInstallDate);
    }

    [TestMethod]
    public void StateJsonOmitsNullInstalledOnComponentPackages()
    {
        VsWhereState state = new VsWhereState
        {
            Product = new VsWherePackage
            {
                Id = "Microsoft.VisualStudio.Product.BuildTools",
                Version = "17.14.31.0",
                Type = "Product",
                Installed = true
            },
            SelectedPackages =
            [
                new VsWherePackage
                {
                    Id = "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
                    Version = "14.44.17.14",
                    Type = "Component"
                }
            ]
        };

        string json = JsonSerializer.Serialize(state);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement component = document.RootElement.GetProperty("selectedPackages").EnumerateArray().Single();

        Assert.IsFalse(component.TryGetProperty("installed", out _));
        Assert.IsTrue(document.RootElement.GetProperty("product").GetProperty("installed").GetBoolean());
    }

    [TestMethod]
    public void QtCreatorDetectionArgumentsReturnJsonInstance()
    {
        using var layout = FakeVsWhereLayout.Create();

        var result = Run(layout.InstallerDirectory, [
            "-products",
            "*",
            "-prerelease",
            "-legacy",
            "-format",
            "json",
            "-utf8"]);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        using var document = JsonDocument.Parse(result.Output);
        var instance = document.RootElement.EnumerateArray().Single();
        Assert.AreEqual(layout.BuildToolsDirectory, instance.GetProperty("installationPath").GetString());
        Assert.AreEqual("Visual Studio Build Tools 17", instance.GetProperty("displayName").GetString());
    }

    [TestMethod]
    public void XmlOutputIncludesProductPathAndCatalog()
    {
        using var layout = FakeVsWhereLayout.Create();

        var result = Run(layout.InstallerDirectory, ["-all", "-format", "xml", "-utf8", "-products", "*"]);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        StringAssert.Contains(result.Output, "<productPath>" + layout.LaunchDevCmdPath + "</productPath>");
        StringAssert.Contains(result.Output, "<isComplete>1</isComplete>");
        StringAssert.Contains(result.Output, "<catalog>");
        StringAssert.Contains(result.Output, "<productLineVersion>17</productLineVersion>");
    }

    [TestMethod]
    public void TextOutputSupportsVsWhereRequiresQuery()
    {
        using var layout = FakeVsWhereLayout.Create();

        var result = Run(layout.InstallerDirectory, [
            "-latest",
            "-products",
            "*",
            "-requires",
            "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
            "-format",
            "text",
            "-nologo"]);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        StringAssert.Contains(result.Output, "installationPath: " + layout.BuildToolsDirectory);
        StringAssert.Contains(result.Output, "installationVersion: 17.14.31.0");
        StringAssert.Contains(result.Output, "productId: Microsoft.VisualStudio.Product.BuildTools");
        Assert.IsFalse(result.Output.TrimStart().StartsWith("[", StringComparison.Ordinal), result.Output);
    }

    [TestMethod]
    public void FindClangClReturnsJsonStringArray()
    {
        using var layout = FakeVsWhereLayout.Create();
        var clangCl = Path.Combine(layout.BuildToolsDirectory, "VC", "Tools", "Llvm", "x64", "bin", "clang-cl.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(clangCl)!);
        File.WriteAllText(clangCl, "");

        var result = Run(layout.InstallerDirectory, [
            "-products",
            "*",
            "-format",
            "json",
            "-utf8",
            "-find",
            @"**\clang-cl.exe"]);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        using var document = JsonDocument.Parse(result.Output);
        Assert.AreEqual(clangCl, document.RootElement.EnumerateArray().Single().GetString());
    }

    [TestMethod]
    public void MissingRequiredComponentReturnsEmptyJsonArray()
    {
        using var layout = FakeVsWhereLayout.Create();

        var result = Run(layout.InstallerDirectory, [
            "-requires",
            "Microsoft.Component.MSBuild",
            "-format",
            "json"]);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.AreEqual("[]", result.Output.Trim());
    }

    [TestMethod]
    public void RequiresArm64ToolsComponentReturnsInstanceWhenStateContainsArm64Component()
    {
        using var layout = FakeVsWhereLayout.Create([
            new VsWherePackage
            {
                Id = "Microsoft.VisualStudio.Component.VC.Tools.ARM64",
                Version = "18.5.11709.182",
                Type = "Component"
            }
        ]);

        var result = Run(layout.InstallerDirectory, [
            "-latest",
            "-prerelease",
            "-products",
            "*",
            "-requires",
            "Microsoft.VisualStudio.Component.VC.Tools.ARM64",
            "-property",
            "installationPath"]);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.AreEqual(layout.BuildToolsDirectory, result.Output.Trim());
    }

    [TestMethod]
    public void VersionRangeFilterMatchingVersionReturnsInstance()
    {
        using var layout = FakeVsWhereLayout.Create();

        var result = Run(layout.InstallerDirectory, ["-version", "[17.0,18.0)", "-format", "json"]);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        using var document = JsonDocument.Parse(result.Output);
        Assert.AreEqual(1, document.RootElement.GetArrayLength());
    }

    [TestMethod]
    public void VersionRangeFilterExcludingVersionReturnsEmpty()
    {
        using var layout = FakeVsWhereLayout.Create();

        var result = Run(layout.InstallerDirectory, ["-version", "[16.0,17.0)", "-format", "json"]);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.AreEqual("[]", result.Output.Trim());
    }

    [TestMethod]
    public void VersionPrefixFilterMatchingVersionReturnsInstance()
    {
        using var layout = FakeVsWhereLayout.Create();

        var result = Run(layout.InstallerDirectory, ["-version", "17", "-format", "json"]);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        using var document = JsonDocument.Parse(result.Output);
        Assert.AreEqual(1, document.RootElement.GetArrayLength());
    }

    [TestMethod]
    public void EmptyResultWithXmlFormatReturnsValidXml()
    {
        using var layout = FakeVsWhereLayout.Create();

        var result = Run(layout.InstallerDirectory, ["-version", "[16.0,17.0)", "-format", "xml"]);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        StringAssert.Contains(result.Output, "<instances>");
        StringAssert.Contains(result.Output, "</instances>");
    }

    private static (int ExitCode, string Output, string Error) Run(string installerDirectory, string[] args)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using StringWriter output = new StringWriter();
        using StringWriter error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            int exitCode = FakeVsWhere.Run(args, installerDirectory);
            return (exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static bool TryCreateJunction(string linkPath, string targetPath, out string error)
    {
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        })!;
        process.WaitForExit();
        error = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        return process.ExitCode == 0;
    }

    private sealed class FakeVsWhereLayout : IDisposable
    {
        private readonly string _root;

        public string BuildToolsDirectory { get; }

        public string InstallerDirectory { get; }

        public string LaunchDevCmdPath => Path.Combine(BuildToolsDirectory, "Common7", "Tools", "LaunchDevCmd.bat");

        private FakeVsWhereLayout(string root)
        {
            _root = root;
            BuildToolsDirectory = Path.Combine(root, "BuildTools");
            InstallerDirectory = Path.Combine(root, "VisualStudio", "Installer");
        }

        public static FakeVsWhereLayout Create(IReadOnlyList<VsWherePackage>? additionalComponents = null)
        {
            string root = Path.Combine(Path.GetTempPath(), "PortableMSVC.Tests.Work", Guid.NewGuid().ToString("N"));
            FakeVsWhereLayout layout = new FakeVsWhereLayout(root);
            Directory.CreateDirectory(layout.BuildToolsDirectory);
            Directory.CreateDirectory(layout.InstallerDirectory);
            string packages = Path.Combine(root, "VisualStudio", "Packages");
            Directory.CreateDirectory(packages);
            VsWherePackage vcTools = new VsWherePackage
            {
                Id = "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
                Version = "14.44.17.14",
                Type = "Component"
            };
            VsWherePackage product = new VsWherePackage
            {
                Id = "Microsoft.VisualStudio.Product.BuildTools",
                Version = "17.14.31.0",
                Type = "Product",
                Installed = true
            };
            List<VsWherePackage> selectedPackages = [vcTools, product];
            if (additionalComponents != null)
            {
                selectedPackages.InsertRange(1, additionalComponents);
            }

            VsWhereState state = new VsWhereState
            {
                InstallationName = "VisualStudio",
                InstallationPath = "BuildTools",
                InstallationVersion = "17.14.31.0",
                InstallDate = "2026-04-29T00:00:00Z",
                LaunchParams = new VsWhereLaunchParams { FileName = @"Common7\Tools\LaunchDevCmd.bat" },
                CatalogInfo = new VsWhereCatalogInfo
                {
                    Id = "VisualStudio",
                    BuildVersion = "17.14.31.0",
                    ProductDisplayVersion = "17.14.31",
                    ProductLine = "Dev17",
                    ProductLineVersion = "17",
                    ProductName = "Visual Studio",
                    ProductSemanticVersion = "17.14.31+0"
                },
                Product = product,
                SelectedPackages = selectedPackages,
                LocalizedResources =
                [
                    new VsWhereLocalizedResource
                    {
                        Language = "en-us",
                        Title = "Visual Studio Build Tools 17",
                        Description = "Build tools",
                        License = "https://go.microsoft.com/fwlink/?LinkId=2179911"
                    }
                ]
            };
            File.WriteAllText(Path.Combine(packages, "state.json"), JsonSerializer.Serialize(state));
            return layout;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
