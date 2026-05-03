namespace PortableMSVC;

public sealed partial class InstallRunner
{
	private static void GenerateRuntimeDebugInstallerScripts(string root, InstallPlan plan)
	{
		foreach (string target in plan.Targets.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			string directory = Path.Combine(root, "Runtime", target);
			string? msi = Directory.Exists(directory)
				? Directory.GetFiles(directory, "vc_RuntimeDebug*.msi", SearchOption.TopDirectoryOnly)
					.OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase)
					.FirstOrDefault()
				: null;
			string cab = Path.Combine(directory, "cab1.cab");
			if (msi == null || !File.Exists(cab))
			{
				continue;
			}

			File.WriteAllText(Path.Combine(directory, "Install_vc_RuntimeDebug.bat"), RuntimeDebugInstallerScript());
		}
	}

	private static string RuntimeDebugInstallerScript()
	{
		return """
@echo off
title Install VC Runtime Debug
pushd "%~dp0"
set "msi=vc_RuntimeDebug.msi"
if exist "vc_RuntimeDebug_arm.msi" set "msi=vc_RuntimeDebug_arm.msi"
if exist "vc_RuntimeDebug_arm64.msi" set "msi=vc_RuntimeDebug_arm64.msi"
if not exist "%msi%" (
echo VC Runtime Debug MSI not found.
pause
exit /b 1
)
if not exist "cab1.cab" (
echo cab1.cab not found.
pause
exit /b 1
)
msiexec /i "%msi%" VSEXTUI=1
set "exitCode=%ERRORLEVEL%"
popd
exit /b %exitCode%
""";
	}

	private static void CopyUcrtDebugRuntimeDlls(string root, InstallPlan plan)
	{
		string kit = Path.Combine(root, "Windows Kits", "10");
		string bin = Path.Combine(kit, "bin");
		string redist = Path.Combine(kit, "Redist");
		string? binVersion = FindVersionDirectory(bin, plan.SdkVersion);
		if (binVersion == null)
		{
			throw new DirectoryNotFoundException("Windows SDK bin 版本目录未找到: " + plan.SdkVersion);
		}

		string redistVersion = FindVersionDirectory(redist, plan.SdkVersion) ?? binVersion;
		foreach (string target in plan.Targets.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			string source = Path.Combine(bin, binVersion, target, "ucrt", "ucrtbased.dll");
			if (!File.Exists(source))
			{
				throw new FileNotFoundException("Windows SDK bin 中未找到 ucrtbased.dll: " + target, source);
			}

			string destination = Path.Combine(redist, redistVersion, "ucrt", "DLLs", target, "ucrtbased.dll");
			Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? redist);
			File.Copy(source, destination, overwrite: true);
		}
	}

	private static void CopyRuntimeDlls(string root, InstallPlan plan)
	{
		string msvcRoot = Path.Combine(root, "BuildTools", "VC", "Tools", "MSVC");
		string? toolsVersion = FindLatestSubdirectory(msvcRoot);
		if (toolsVersion == null)
		{
			return;
		}

		string? redistRoot = FindLatestSubdirectoryPath(Path.Combine(root, "BuildTools", "VC", "Redist", "MSVC"));
		string windowsKit = Path.Combine(root, "Windows Kits", "10");
		string? sdkRedistVersion = FindVersionDirectory(Path.Combine(windowsKit, "Redist"), plan.SdkVersion);

		foreach (string target in plan.Targets)
		{
			string destination = Path.Combine(msvcRoot, toolsVersion, "bin", "Host" + plan.Host, target);
			Directory.CreateDirectory(destination);

			if (redistRoot != null)
			{
				CopyMsvcRedistDlls(redistRoot, target, destination);
			}

			if (sdkRedistVersion != null)
			{
				CopyUcrtDlls(Path.Combine(windowsKit, "Redist", sdkRedistVersion, "ucrt", "DLLs", target), destination);
			}

			CopyTopLevelDlls(Path.Combine(windowsKit, "Redist", "D3D", target), destination);
			CopyTopLevelDlls(Path.Combine(windowsKit, "Redist", "MBN", target), destination);
		}
	}

	private static void CopyMsvcRedistDlls(string redistRoot, string target, string destination)
	{
		foreach (string file in Directory.GetFiles(redistRoot, "*.dll", SearchOption.AllDirectories))
		{
			string relative = Path.GetRelativePath(redistRoot, file);
			if (PathSegmentsContain(relative, target))
			{
				CopyFileIfMissing(file, Path.Combine(destination, Path.GetFileName(file)));
			}
		}
	}

	private static bool PathSegmentsContain(string relativePath, string segment)
	{
		foreach (string part in relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
		{
			if (part.Equals(segment, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private static void CopyUcrtDlls(string sourceDirectory, string destination)
	{
		if (!Directory.Exists(sourceDirectory))
		{
			return;
		}

		foreach (string file in Directory.GetFiles(sourceDirectory, "*.dll", SearchOption.TopDirectoryOnly))
		{
			string name = Path.GetFileName(file);
			if (name.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			CopyFileIfMissing(file, Path.Combine(destination, name));
		}
	}

	private static void CopyTopLevelDlls(string sourceDirectory, string destination)
	{
		if (!Directory.Exists(sourceDirectory))
		{
			return;
		}

		foreach (string file in Directory.GetFiles(sourceDirectory, "*.dll", SearchOption.TopDirectoryOnly))
		{
			CopyFileIfMissing(file, Path.Combine(destination, Path.GetFileName(file)));
		}
	}

	private static void CopyFileIfMissing(string source, string destination)
	{
		if (!File.Exists(destination))
		{
			File.Copy(source, destination, overwrite: false);
		}
	}
}
