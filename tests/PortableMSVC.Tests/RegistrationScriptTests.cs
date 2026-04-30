using System.Reflection;

namespace PortableMSVC.Tests;

[TestClass]
public sealed class RegistrationScriptTests
{
    [TestMethod]
    public void RegistrationScriptsAreWrittenToPortableRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "PortableMSVC.Tests.Work", Guid.NewGuid().ToString("N"));
        try
        {
            InvokeGenerateScripts(root);

            Assert.IsTrue(File.Exists(Path.Combine(root, "Setup.bat")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "Clean.bat")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "VisualStudio", "Installer", "Setup.bat")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "VisualStudio", "Installer", "Clean.bat")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "VisualStudio", "Installer", "vswhere.bat")));
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
    public void SetupScriptCallsVsWhereSetupAndPassesArguments()
    {
        string script = InvokeStringFactory("RegisterVsWhereScript");

        StringAssert.Contains(script, "\"%~dp0VisualStudio\\Installer\\vswhere.exe\" --setup %*");
        StringAssert.Contains(script, "set \"exitCode=%ERRORLEVEL%\"");
        StringAssert.Contains(script, "exit /b %exitCode%");
    }

    [TestMethod]
    public void CleanScriptCallsVsWhereCleanAndPassesArguments()
    {
        string script = InvokeStringFactory("UnregisterVsWhereScript");

        StringAssert.Contains(script, "\"%~dp0VisualStudio\\Installer\\vswhere.exe\" --clean %*");
        StringAssert.Contains(script, "set \"exitCode=%ERRORLEVEL%\"");
        StringAssert.Contains(script, "exit /b %exitCode%");
    }

    [TestMethod]
    public void PortableSetupProbeIdentifiesFakeVsWhere()
    {
        TextWriter originalOut = Console.Out;
        using StringWriter output = new StringWriter();
        try
        {
            Console.SetOut(output);

            int exitCode = PortableSetupRunner.Run(["--portable-msvc-probe"]);

            Assert.AreEqual(0, exitCode);
            Assert.AreEqual("PortableMSVCFakeVsWhere", output.ToString().Trim());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [TestMethod]
    public void SetupStatusStoresRegistryBackupsPerView()
    {
        var status = new PortableSetupStatus
        {
            RegistryBackups =
            [
                new PortableRegistryViewBackup { View = "Registry32" },
                new PortableRegistryViewBackup { View = "Registry64" }
            ]
        };

        string json = System.Text.Json.JsonSerializer.Serialize(status);

        StringAssert.Contains(json, "RegistryBackups");
        StringAssert.Contains(json, "Registry32");
        StringAssert.Contains(json, "Registry64");
    }

    [TestMethod]
    public void SetupInstallerJunctionRequiresForceForExistingInstallerDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "PortableMSVC.Tests.Work", Guid.NewGuid().ToString("N"));
        string portableInstaller = Path.Combine(root, "portable", "VisualStudio", "Installer");
        string installerTarget = Path.Combine(root, "program-files", "Microsoft Visual Studio", "Installer");
        string marker = Path.Combine(installerTarget, "real-installer.txt");
        try
        {
            Directory.CreateDirectory(portableInstaller);
            Directory.CreateDirectory(installerTarget);
            File.WriteAllText(marker, "existing");

            object context = CreateSetupContext(
                portableRoot: Path.Combine(root, "portable"),
                visualStudioDirectory: Path.Combine(root, "portable", "VisualStudio"),
                portableInstaller: portableInstaller,
                visualStudioProgramFilesRoot: Path.Combine(root, "program-files", "Microsoft Visual Studio"),
                installerTarget: installerTarget);
            var status = new PortableSetupStatus();

            var ex = Assert.ThrowsExactly<TargetInvocationException>(() =>
                typeof(PortableSetupRunner)
                    .GetMethod("SetupInstallerJunction", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, [context, status, false]));

            Assert.IsInstanceOfType<InvalidOperationException>(ex.InnerException);
            StringAssert.Contains(ex.InnerException!.Message, "--setup --force");
            Assert.IsTrue(Directory.Exists(installerTarget));
            Assert.IsTrue(File.Exists(marker));
            Assert.IsFalse(Directory.Exists(installerTarget + ".PortableMSVCBackup"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void InvokeGenerateScripts(string root)
    {
        typeof(InstallRunner)
            .GetMethod("GenerateVsWhereRegistrationScripts", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [root]);
    }

    private static string InvokeStringFactory(string methodName)
    {
        return (string)typeof(InstallRunner)
            .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [])!;
    }

    private static object CreateSetupContext(
        string portableRoot,
        string visualStudioDirectory,
        string portableInstaller,
        string visualStudioProgramFilesRoot,
        string installerTarget)
    {
        Type contextType = typeof(PortableSetupRunner).GetNestedType("SetupContext", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(PortableSetupRunner), "SetupContext");
        object context = Activator.CreateInstance(contextType, nonPublic: true)!;
        Set(context, "PortableRoot", portableRoot);
        Set(context, "VisualStudioDirectory", visualStudioDirectory);
        Set(context, "PortableInstaller", portableInstaller);
        Set(context, "VisualStudioProgramFilesRoot", visualStudioProgramFilesRoot);
        Set(context, "InstallerTarget", installerTarget);
        Set(context, "WindowsSdkRoot", Path.Combine(portableRoot, "Windows Kits", "10") + Path.DirectorySeparatorChar);
        Set(context, "StatusPath", Path.Combine(visualStudioDirectory, "Setup", "status.json"));
        return context;

        static void Set(object instance, string propertyName, string value)
        {
            instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)!
                .SetValue(instance, value);
        }
    }
}
