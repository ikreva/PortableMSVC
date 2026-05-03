namespace PortableMSVC;

public sealed partial class InstallRunner
{
	private static void CleanupInstalledFiles(string root, InstallPlan plan)
	{
		CleanupCommon7(root);
		CleanupVcAuxiliaryBuild(root);
		CleanupMsvcTools(root, plan);
		CleanupWindowsKits(root, plan);
	}

	private static void CleanupCommon7(string root)
	{
		DeleteDirectory(Path.Combine(root, "BuildTools", "Common7", "IDE"));

		string tools = Path.Combine(root, "BuildTools", "Common7", "Tools");
		if (!Directory.Exists(tools))
		{
			return;
		}

		foreach (string directory in Directory.GetDirectories(tools))
		{
			if (!Path.GetFileName(directory).Equals("vsdevcmd", StringComparison.OrdinalIgnoreCase))
			{
				DeleteDirectory(directory);
			}
		}

		foreach (string file in Directory.GetFiles(tools))
		{
			string name = Path.GetFileName(file);
			if (!name.Equals("LaunchDevCmd.bat", StringComparison.OrdinalIgnoreCase) &&
				!name.Equals("VsDevCmd.bat", StringComparison.OrdinalIgnoreCase))
			{
				DeleteFile(file);
			}
		}

		DeleteDirectory(Path.Combine(tools, "vsdevcmd", "ext", "vcvars"));
	}

	private static void CleanupVcAuxiliaryBuild(string root)
	{
		string build = Path.Combine(root, "BuildTools", "VC", "Auxiliary", "Build");
		if (!Directory.Exists(build))
		{
			return;
		}

		foreach (string directory in Directory.GetDirectories(build))
		{
			DeleteDirectory(directory);
		}

		foreach (string props in Directory.GetFiles(build, "*.props", SearchOption.TopDirectoryOnly))
		{
			DeleteFile(props);
		}
	}

	private static void CleanupMsvcTools(string root, InstallPlan plan)
	{
		string msvcRoot = Path.Combine(root, "BuildTools", "VC", "Tools", "MSVC");
		if (!Directory.Exists(msvcRoot))
		{
			return;
		}

		string wantedHostDirectoryName = "Host" + plan.Host;
		HashSet<string> wantedTargets = new(plan.Targets, StringComparer.OrdinalIgnoreCase);
		foreach (string versionDirectory in Directory.GetDirectories(msvcRoot))
		{
			DeleteDirectory(Path.Combine(versionDirectory, "Auxiliary"));

			string bin = Path.Combine(versionDirectory, "bin");
			if (!Directory.Exists(bin))
			{
				continue;
			}

			foreach (string hostDirectory in Directory.GetDirectories(bin))
			{
				string hostName = Path.GetFileName(hostDirectory);
				if (IsKnownHostDirectory(hostName) && !hostName.Equals(wantedHostDirectoryName, StringComparison.OrdinalIgnoreCase))
				{
					DeleteDirectory(hostDirectory);
					continue;
				}

				if (hostName.Equals(wantedHostDirectoryName, StringComparison.OrdinalIgnoreCase))
				{
					DeleteKnownArchitectureDirectoriesExcept(hostDirectory, wantedTargets);
					DeleteTargetSubdirectories(hostDirectory, wantedTargets, "onecore");
					DeleteTargetFiles(hostDirectory, wantedTargets, "vctip.exe", "vctip.exe.config");
				}
			}

			CleanupMsvcLib(Path.Combine(versionDirectory, "lib"), wantedTargets);
			CleanupMsvcLib(Path.Combine(versionDirectory, "atlmfc", "lib"), wantedTargets);
		}
	}

	private static void CleanupMsvcLib(string libDirectory, HashSet<string> wantedTargets)
	{
		if (!Directory.Exists(libDirectory))
		{
			return;
		}

		DeleteKnownArchitectureDirectoriesExcept(libDirectory, wantedTargets);
		DeleteTargetSubdirectories(libDirectory, wantedTargets, "store", "uwp", "enclave", "onecore");
	}

	private static void CleanupWindowsKits(string root, InstallPlan plan)
	{
		string kit = Path.Combine(root, "Windows Kits", "10");
		DeleteDirectory(Path.Combine(kit, "Catalogs"));
		DeleteDirectory(Path.Combine(kit, "DesignTime"));
		CleanupWindowsKitBin(kit, plan);
		CleanupWindowsKitLib(kit, plan);
		CleanupWindowsKitRedist(kit, plan);
	}

	private static void CleanupWindowsKitBin(string kit, InstallPlan plan)
	{
		string bin = Path.Combine(kit, "bin");
		if (!Directory.Exists(bin))
		{
			return;
		}

		foreach (string versionDirectory in Directory.GetDirectories(bin))
		{
			foreach (string architecture in ArchitectureNames)
			{
				string architectureDirectory = Path.Combine(versionDirectory, architecture);
				if (architecture.Equals(plan.Host, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				DeleteDirectory(architectureDirectory);
			}
			DeleteDirectory(Path.Combine(versionDirectory, "chpe"));
		}
	}

	private static void CleanupWindowsKitLib(string kit, InstallPlan plan)
	{
		string lib = Path.Combine(kit, "Lib");
		if (!Directory.Exists(lib))
		{
			return;
		}

		HashSet<string> wantedTargets = new(plan.Targets, StringComparer.OrdinalIgnoreCase);
		foreach (string versionDirectory in Directory.GetDirectories(lib))
		{
			DeleteDirectory(Path.Combine(versionDirectory, "ucrt_enclave"));
			foreach (string libraryKindDirectory in Directory.GetDirectories(versionDirectory))
			{
				DeleteKnownArchitectureDirectoriesExcept(libraryKindDirectory, wantedTargets);
			}
		}
	}

	private static void CleanupWindowsKitRedist(string kit, InstallPlan plan)
	{
		string redist = Path.Combine(kit, "Redist");
		if (!Directory.Exists(redist))
		{
			return;
		}

		HashSet<string> wantedTargets = new(plan.Targets, StringComparer.OrdinalIgnoreCase);
		foreach (string directory in Directory.GetDirectories(redist, "*", SearchOption.AllDirectories))
		{
			DeleteKnownArchitectureDirectoriesExcept(directory, wantedTargets);
		}
	}

	private static void DeleteKnownArchitectureDirectoriesExcept(string parent, HashSet<string> wanted)
	{
		foreach (string architecture in ArchitectureNames)
		{
			if (!wanted.Contains(architecture))
			{
				DeleteDirectory(Path.Combine(parent, architecture));
			}
		}
	}

	private static void DeleteTargetSubdirectories(string parent, HashSet<string> wantedTargets, params string[] subdirectories)
	{
		foreach (string target in wantedTargets)
		{
			foreach (string subdirectory in subdirectories)
			{
				DeleteDirectory(Path.Combine(parent, target, subdirectory));
			}
		}
	}

	private static void DeleteTargetFiles(string parent, HashSet<string> wantedTargets, params string[] fileNames)
	{
		foreach (string target in wantedTargets)
		{
			foreach (string fileName in fileNames)
			{
				DeleteFile(Path.Combine(parent, target, fileName));
			}
		}
	}

	private static bool IsKnownHostDirectory(string name)
	{
		return name.Equals("Hostx86", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("Hostx64", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("Hostarm64", StringComparison.OrdinalIgnoreCase);
	}
}
