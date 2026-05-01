using System.Text.Encodings.Web;
using System.Text.Json;

namespace PortableMSVC;

public sealed class InstallRunner
{
	private const int MaxParallelDownloads = 6;

	private static readonly string[] ArchitectureNames = ["x86", "x64", "arm", "arm64"];

	private readonly Downloader _downloader = new Downloader();

	private readonly MsiExtractor _msiExtractor = new MsiExtractor();

	private static readonly JsonSerializerOptions ReadableJsonOptions = new JsonSerializerOptions
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		WriteIndented = true
	};

	public async Task RunAsync(PackageIndex index, InstallPlan plan, string outputDirectory, string cacheDirectory, bool copyRuntimeDlls, CancellationToken cancellationToken)
	{
		if (!plan.Success)
		{
			throw new InvalidOperationException("安装计划包含错误，拒绝执行安装。");
		}
		string root = Path.GetFullPath(outputDirectory);
		string buildTools = Path.Combine(root, "BuildTools");
		string cache = Path.GetFullPath(cacheDirectory);
		string installers = Path.Combine(cache, "installers");
		Directory.CreateDirectory(root);
		Directory.CreateDirectory(buildTools);
		Directory.CreateDirectory(installers);
		// 先下载、校验，再统一解包。这样半成品下载不会进入输出目录，
		// SHA 校验失败也能在安装早期停止。
		IReadOnlyList<DownloadedFile> vsixFiles = await DownloadAllAsync(plan.Payloads, payload => _downloader.DownloadAsync(payload, cache, cancellationToken), cancellationToken);
		foreach (DownloadedFile file in vsixFiles.Where(file => Path.GetExtension(file.Path).Equals(".vsix", StringComparison.OrdinalIgnoreCase)))
		{
			VsixExtractor.ExtractContents(file.Path, buildTools);
			Downloader.LogStatus("解包", file.DisplayName, "OK");
		}
		await DownloadAndExtractSdkAsync(index, plan, root, installers, cancellationToken);
		await DownloadRuntimeInstallersAsync(plan, root, cache, cancellationToken);
		CopyMsdiaAndRemoveDia(root, plan);
		WriteDefaultVersionFiles(root, plan);
		WriteVisualStudioMetadata(root, index, plan);
		InstallFakeVsWhere(root);
		PatchWinSdkBat(root);
		PatchVsDevCmdBat(root);
		GenerateCommandPromptScripts(root, plan);
		GenerateVsWhereRegistrationScripts(root);
		if (copyRuntimeDlls)
		{
			CopyRuntimeDlls(root, plan);
		}
		CleanupInstalledFiles(root, plan);
	}

	private async Task DownloadAndExtractSdkAsync(PackageIndex index, InstallPlan plan, string root, string installers, CancellationToken cancellationToken)
	{
		PackageInfo sdkPackage = index.FindSdkPayloadPackage(plan.SdkVersion) ?? throw new InvalidOperationException("SDK payload 包未找到: " + plan.SdkVersion);
		Dictionary<string, PayloadInfo> payloads = sdkPackage.Payloads.ToDictionary(x => x.FileName.Replace('/', '\\'), StringComparer.OrdinalIgnoreCase);
		List<(string FileName, PayloadInfo Payload)> msiPayloads = new();
		SortedSet<string> cabinetNames = new(StringComparer.OrdinalIgnoreCase);

		// MSI 的 Media 表列出解包所需 CAB。先下载所有 MSI 并读取 Media，
		// 再一次性下载共享 CAB 集合。
		foreach (string fileName in plan.SdkPayloads.Where(x => x.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)))
		{
			if (!payloads.TryGetValue(fileName, out var payload))
			{
				throw new InvalidOperationException("SDK payload 在 manifest 中不存在: " + fileName);
			}
			msiPayloads.Add((fileName, payload));
		}
		IReadOnlyList<DownloadedFile> downloadedMsiPaths = await DownloadAllAsync(msiPayloads, item => _downloader.DownloadSdkPayloadAsync(item.Payload, installers, cancellationToken), cancellationToken);
		List<string> msiPaths = new();
		for (int i = 0; i < msiPayloads.Count; i++)
		{
			string fileName = msiPayloads[i].FileName;
			DownloadedFile downloaded = downloadedMsiPaths[i];
			string msiPath = Path.Combine(installers, Path.GetFileName(fileName));
			File.Copy(downloaded.Path, msiPath, overwrite: true);
			msiPaths.Add(msiPath);
			foreach (string cab in _msiExtractor.GetCabinetNames(msiPath))
			{
				cabinetNames.Add(cab);
			}
		}
		List<(string Cabinet, PayloadInfo Payload)> cabPayloads = new();
		foreach (string cabinetName in cabinetNames)
		{
			string cabFileName = "Installers\\" + cabinetName;
			if (!payloads.TryGetValue(cabFileName, out var cabPayload))
			{
				throw new InvalidOperationException("SDK CAB 文件在 manifest 中不存在: " + cabFileName);
			}
			cabPayloads.Add((cabinetName, cabPayload));
		}
		IReadOnlyList<DownloadedFile> downloadedCabPaths = await DownloadAllAsync(cabPayloads, item => _downloader.DownloadSdkPayloadAsync(item.Payload, installers, cancellationToken), cancellationToken);
		for (int i = 0; i < cabPayloads.Count; i++)
		{
			string cabinetName = cabPayloads[i].Cabinet;
			DownloadedFile downloadedCab = downloadedCabPaths[i];
			File.Copy(downloadedCab.Path, Path.Combine(installers, cabinetName), overwrite: true);
		}
		foreach (string msiPath in msiPaths)
		{
			_msiExtractor.Extract(msiPath, root, installers);
			Downloader.LogStatus("解包", Path.GetFileName(msiPath), "OK");
		}
		NormalizeSourceDir(root);
		CopyUcrtDebugRuntimeDlls(root, plan);
	}

	private async Task DownloadRuntimeInstallersAsync(InstallPlan plan, string root, string cache, CancellationToken cancellationToken)
	{
		if (plan.RuntimePayloads.Count == 0)
		{
			return;
		}

		IReadOnlyList<DownloadedFile> downloaded = await DownloadAllAsync(plan.RuntimePayloads, payload => _downloader.DownloadAsync(payload, cache, cancellationToken), cancellationToken);
		for (int i = 0; i < plan.RuntimePayloads.Count; i++)
		{
			PlannedPayload payload = plan.RuntimePayloads[i];
			DownloadedFile file = downloaded[i];
			string relativePath = payload.FileName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
			string destination = Path.Combine(root, relativePath);
			Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? root);
			File.Copy(file.Path, destination, overwrite: true);
			Downloader.LogStatus("复制", payload.FileName, "OK");
		}
		GenerateRuntimeDebugInstallerScripts(root, plan);
	}

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

	private static async Task<IReadOnlyList<TResult>> DownloadAllAsync<TItem, TResult>(IReadOnlyList<TItem> items, Func<TItem, Task<TResult>> download, CancellationToken cancellationToken)
	{
		TResult[] results = new TResult[items.Count];
		using SemaphoreSlim semaphore = new(MaxParallelDownloads);
		IEnumerable<Task> tasks = items.Select(async (item, index) =>
		{
			await semaphore.WaitAsync(cancellationToken);
			try
			{
				results[index] = await download(item);
			}
			finally
			{
				semaphore.Release();
			}
		});
		await Task.WhenAll(tasks);
		return results;
	}

	private static void NormalizeSourceDir(string root)
	{
		string sourceDir = Path.Combine(root, "SourceDir");
		if (Directory.Exists(sourceDir))
		{
			string[] directories = Directory.GetDirectories(sourceDir);
			foreach (string directory in directories)
			{
				MergeDirectory(directory, Path.Combine(root, Path.GetFileName(directory)));
			}
			string[] files = Directory.GetFiles(sourceDir);
			foreach (string file in files)
			{
				File.Move(file, Path.Combine(root, Path.GetFileName(file)), overwrite: true);
			}
			DeleteDirectory(sourceDir);
		}
	}

	private static void MergeDirectory(string source, string destination)
	{
		Directory.CreateDirectory(destination);
		string[] directories = Directory.GetDirectories(source);
		foreach (string directory in directories)
		{
			MergeDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
		}
		string[] files = Directory.GetFiles(source);
		foreach (string file in files)
		{
			File.Move(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
		}
		DeleteDirectory(source);
	}

	private static void CopyMsdiaAndRemoveDia(string root, InstallPlan plan)
	{
		string dia = Path.Combine(root, "BuildTools", "DIA%20SDK");
		if (!Directory.Exists(dia))
		{
			return;
		}

		string relative = plan.Host.ToLowerInvariant() switch
		{
			"x86" => Path.Combine("bin", "msdia140.dll"),
			"x64" => Path.Combine("bin", "amd64", "msdia140.dll"),
			"arm" => Path.Combine("bin", "arm", "msdia140.dll"),
			"arm64" => Path.Combine("bin", "arm64", "msdia140.dll"),
			_ => Path.Combine("bin", "amd64", "msdia140.dll"),
		};

		string msvcDirectory = Path.Combine(root, "BuildTools", "VC", "Tools", "MSVC");
		string? msvcVersion = (Directory.Exists(msvcDirectory) ? Directory.GetDirectories(msvcDirectory).Select(Path.GetFileName).OfType<string>()
			.OrderBy(x => x, NaturalVersionComparer.Instance)
			.LastOrDefault() : null);
		string source = Path.Combine(dia, relative);
		if (File.Exists(source) && msvcVersion != null)
		{
			// DIA SDK 不在编译器 bin 布局内；便携包中把 msdia140.dll 复制到
			// 请求的 host/target 工具旁边，方便 cl/link 直接找到。
			foreach (string target in plan.Targets)
			{
				string destination = Path.Combine(msvcDirectory, msvcVersion, "bin", "Host" + plan.Host, target, "msdia140.dll");
				string? destinationDirectory = Path.GetDirectoryName(destination);
				if (destinationDirectory != null)
				{
					Directory.CreateDirectory(destinationDirectory);
				}
				File.Copy(source, destination, overwrite: true);
			}
		}
		DeleteDirectory(dia);
	}

	private static void WriteDefaultVersionFiles(string root, InstallPlan plan)
	{
		string build = Path.Combine(root, "BuildTools", "VC", "Auxiliary", "Build");
		Directory.CreateDirectory(build);
		string toolsVersion = FindLatestSubdirectory(Path.Combine(root, "BuildTools", "VC", "Tools", "MSVC")) ?? plan.MsvcVersion;
		string redistVersion = FindLatestSubdirectory(Path.Combine(root, "BuildTools", "VC", "Redist", "MSVC")) ?? plan.RedistVersion;

		// 官方 vcvars 通过这些文本文件选择已安装的 toolset/redist 版本。
		File.WriteAllText(Path.Combine(build, "Microsoft.VCToolsVersion.default.txt"), toolsVersion);
		File.WriteAllText(Path.Combine(build, "Microsoft.VCRedistVersion.default.txt"), redistVersion);
	}

	private static void WriteVisualStudioMetadata(string root, PackageIndex index, InstallPlan plan)
	{
		string packagesDirectory = Path.Combine(root, "VisualStudio", "Packages");
		Directory.CreateDirectory(packagesDirectory);
		VsWherePackage product = new()
		{
			Id = "Microsoft.VisualStudio.Product.BuildTools",
			Version = index.BuildVersion,
			Type = "Product",
			Installed = true
		};
		List<VsWherePackage> selectedPackages = new(VsWhereComponentSelector.SelectVcToolComponents(index, plan))
		{
			product
		};
		AddSdkComponent(index, plan, selectedPackages);
		VsWhereState state = new VsWhereState
		{
			InstallationName = index.ProductInfoId,
			CatalogInfo = new VsWhereCatalogInfo
			{
				Id = index.ProductInfoId,
				BuildVersion = index.BuildVersion,
				ProductDisplayVersion = index.ProductVersion,
				ProductLine = index.ProductLine,
				ProductLineVersion = index.ProductLineVersion,
				ProductName = "Visual Studio",
				ProductSemanticVersion = index.ProductSemanticVersion
			},
			InstallationPath = "BuildTools",
			LaunchParams = new VsWhereLaunchParams { FileName = @"Common7\Tools\LaunchDevCmd.bat" },
			InstallationVersion = index.BuildVersion,
			InstallDate = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
			Seed = new VsWhereSeed { Languages = ["en-us", "zh-cn"] },
			LocalizedResources =
			{
				new()
				{
					Language = "en-us",
					Title = "Visual Studio Build Tools " + index.ProductLineVersion,
					Description = "The Visual Studio Build Tools allows you to build native and managed MSBuild-based applications without requiring the Visual Studio IDE.",
					License = "https://go.microsoft.com/fwlink/?LinkId=2179911"
				},
				new()
				{
					Language = "zh-cn",
					Title = "Visual Studio 生成工具 " + index.ProductLineVersion,
					Description = "Visual Studio 生成工具允许生成本机和基于 MSBuild 的应用程序，而不需要 Visual Studio IDE。",
					License = "https://go.microsoft.com/fwlink/?LinkId=2179911"
				}
			},
			Product = product,
			SelectedPackages = selectedPackages
		};
		PortableMSVCJsonContext jsonContext = new PortableMSVCJsonContext(ReadableJsonOptions);
		File.WriteAllText(Path.Combine(packagesDirectory, "state.json"), JsonSerializer.Serialize(state, jsonContext.VsWhereState));
	}

	private static void AddSdkComponent(PackageIndex index, InstallPlan plan, List<VsWherePackage> selectedPackages)
	{
		string sdkBuild = plan.SdkVersion.Split('.').Last();
		PackageInfo? sdkComponent = index.Find("Microsoft.VisualStudio.Component.Windows11SDK." + sdkBuild)
			?? index.Find("Microsoft.VisualStudio.Component.Windows10SDK." + sdkBuild);
		if (sdkComponent != null)
		{
			selectedPackages.Add(new VsWherePackage
			{
				Id = sdkComponent.Id,
				Version = sdkComponent.Version ?? plan.SdkVersion,
				Type = "Component"
			});
		}
	}

	private static void InstallFakeVsWhere(string root)
	{
		string installerDirectory = Path.Combine(root, "VisualStudio", "Installer");
		Directory.CreateDirectory(installerDirectory);
		string destination = Path.Combine(installerDirectory, "vswhere.exe");
		string? processPath = Environment.ProcessPath;
		if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
		{
			Console.Error.WriteLine("当前进程路径不可用，未生成 fake vswhere.exe。");
			return;
		}
		if (FakeVsWhere.IsVsWhereProcess())
		{
			Console.Error.WriteLine("当前进程已经是 vswhere.exe，跳过生成 fake vswhere.exe。");
			return;
		}
		File.Copy(processPath, destination, overwrite: true);
	}

	private static void GenerateCommandPromptScripts(string root, InstallPlan plan)
	{
		string scripts = Path.Combine(root, "Scripts");
		Directory.CreateDirectory(scripts);
		string productLine = ProductLineVersion(plan);
		foreach (string target in plan.Targets)
		{
			(string scriptName, string titlePrefix) = ScriptNames(plan.Host, target);
			string vcvarsScript = VcvarsScriptName(plan.Host, target);
			File.WriteAllText(Path.Combine(scripts, scriptName), CommandPromptScriptContent(titlePrefix, productLine, vcvarsScript));
		}
	}

	private static void GenerateVsWhereRegistrationScripts(string root)
	{
		string installer = Path.Combine(root, "VisualStudio", "Installer");
		Directory.CreateDirectory(installer);
		File.WriteAllText(Path.Combine(root, "Setup.bat"), RegisterVsWhereScript());
		File.WriteAllText(Path.Combine(root, "Clean.bat"), UnregisterVsWhereScript());
		File.WriteAllText(Path.Combine(installer, "vswhere.bat"), VsWhereTestScript());
	}

	private static string? FindLatestSubdirectory(string directory)
	{
		return Directory.Exists(directory) ? Directory.GetDirectories(directory).Select(Path.GetFileName).OfType<string>()
			.OrderBy(x => x, NaturalVersionComparer.Instance)
			.LastOrDefault() : null;
	}

	private static void DeleteDirectory(string path)
	{
		if (Directory.Exists(path))
		{
			Directory.Delete(path, recursive: true);
		}
	}

	private static void DeleteFile(string path)
	{
		if (File.Exists(path))
		{
			File.Delete(path);
		}
	}

	private static void CleanupInstalledFiles(string root, InstallPlan plan)
	{
		CleanupCommon7(root);
		CleanupVcAuxiliaryBuild(root);
		CleanupMsvcTools(root, plan);
		CleanupWindowsKits(root, plan);
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

	private static string? FindLatestSubdirectoryPath(string directory)
	{
		string? name = FindLatestSubdirectory(directory);
		return name == null ? null : Path.Combine(directory, name);
	}

	private static string? FindVersionDirectory(string parent, string versionPrefix)
	{
		if (!Directory.Exists(parent))
		{
			return null;
		}

		return Directory.GetDirectories(parent)
			.Select(Path.GetFileName)
			.OfType<string>()
			.Where(x => x.Equals(versionPrefix, StringComparison.OrdinalIgnoreCase) || x.StartsWith(versionPrefix + ".", StringComparison.OrdinalIgnoreCase))
			.OrderBy(x => x, NaturalVersionComparer.Instance)
			.LastOrDefault();
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

	private static string CommandPromptScriptContent(string titlePrefix, string productLine, string vcvarsScript)
	{
		return $"@echo off\r\ntitle {titlePrefix} Tools Command Prompt For Visual Studio {productLine}\r\n\"%ComSpec%\" /k \"\"%~dp0..\\BuildTools\\VC\\Auxiliary\\Build\\{vcvarsScript}\"\"\r\n";
	}

	private static (string ScriptName, string TitlePrefix) ScriptNames(string host, string target)
	{
		return (host.ToLowerInvariant(), target.ToLowerInvariant()) switch
		{
			("x64", "x64") => ("x64_NativeToolsCommandPrompt.bat", "x64 Native"),
			("x86", "x86") => ("x86_NativeToolsCommandPrompt.bat", "x86 Native"),
			("arm64", "arm64") => ("arm64_NativeToolsCommandPrompt.bat", "arm64 Native"),
			_ => (host + "_" + target + "_CrossToolsCommandPrompt.bat", host + "_" + target + " Cross"),
		};
	}

	private static string VcvarsScriptName(string host, string target)
	{
		return (host.ToLowerInvariant(), target.ToLowerInvariant()) switch
		{
			("x64", "x64") => "vcvars64.bat",
			("x64", "x86") => "vcvarsamd64_x86.bat",
			("x64", "arm") => "vcvarsamd64_arm.bat",
			("x64", "arm64") => "vcvarsamd64_arm64.bat",
			("x86", "x86") => "vcvars32.bat",
			("x86", "x64") => "vcvarsx86_amd64.bat",
			("x86", "arm") => "vcvarsx86_arm.bat",
			("x86", "arm64") => "vcvarsx86_arm64.bat",
			("arm64", "arm64") => "vcvarsarm64.bat",
			("arm64", "x64") => "vcvarsarm64_amd64.bat",
			("arm64", "x86") => "vcvarsarm64_x86.bat",
			("arm64", "arm") => "vcvarsarm64_arm.bat",
			_ => "vcvarsall.bat",
		};
	}

	private static string RegisterVsWhereScript()
	{
		return """
@echo off
"%~dp0VisualStudio\Installer\vswhere.exe" --setup %*
set "exitCode=%ERRORLEVEL%"
if "%1" NEQ "skip_pause" pause
exit /b %exitCode%
""";
	}

	private static string UnregisterVsWhereScript()
	{
		return """
@echo off
"%~dp0VisualStudio\Installer\vswhere.exe" --clean %*
set "exitCode=%ERRORLEVEL%"
if "%1" NEQ "skip_pause" pause
exit /b %exitCode%
""";
	}

	private static string VsWhereTestScript()
	{
		return """
@"%~dp0vswhere.exe" -all -format xml -utf8 -products *
@pause>nul
""";
	}

	private static void PatchWinSdkBat(string root)
	{
		string path = Path.Combine(root, "BuildTools", "Common7", "Tools", "vsdevcmd", "core", "winsdk.bat");
		if (!File.Exists(path))
		{
			return;
		}

		string text = File.ReadAllText(path);

		// 用本体函数中特有的字符串做幂等检查，避免与 "call :GetPortableWinSdkDir" 误匹配。
		if (text.Contains("WindowsSdkDir=%VSINSTALLDIR%..\\.\\Windows Kits\\10\\", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("WindowsSdkDir=%VSINSTALLDIR%..\\Windows Kits\\10\\", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		// 保留官方 vcvars/VsDevCmd 脚本，只让 SDK 查找先尝试便携 Windows Kits，
		// 再回退到原有注册表探测。
		text = InsertAfterLabel(text, ":GetWin10SdkDir", "call :GetPortableWinSdkDir > nul 2>&1\r\nif not errorlevel 1 exit /B 0\r\n");
		text = InsertBeforeLabel(text, ":GetWin10SdkDirHelper", PortableWinSdkDirHelper() + "\r\n");
		text = InsertAfterLabel(text, ":GetUniversalCRTSdkDir", "call :GetPortableUniversalCRTSdkDir > nul 2>&1\r\nif not errorlevel 1 exit /B 0\r\n");
		text = InsertBeforeLabel(text, ":GetUniversalCRTSdkDirHelper", PortableUniversalCrtSdkDirHelper() + "\r\n");
		File.WriteAllText(path, text);
	}

	private static void PatchVsDevCmdBat(string root)
	{
		string path = Path.Combine(root, "BuildTools", "Common7", "Tools", "VsDevCmd.bat");
		if (!File.Exists(path))
		{
			return;
		}

		string text = File.ReadAllText(path);
		text = RemoveBetween(
			text,
			"@REM Send Telemetry if user's VS is opted-in",
			"@REM Script clean-up of environment variables used to track",
			string.Empty);
		File.WriteAllText(path, text);
	}

	private static string InsertAfterLabel(string text, string label, string insertion)
	{
		(int lineStart, int lineEnd) = FindLabelLine(text, label);
		if (lineStart < 0)
		{
			return text;
		}

		int insertAt = lineEnd < text.Length && text[lineEnd] == '\n' ? lineEnd + 1 : lineEnd;
		return text.Insert(insertAt, insertion);
	}

	private static string InsertBeforeLabel(string text, string label, string insertion)
	{
		(int lineStart, _) = FindLabelLine(text, label);
		if (lineStart < 0)
		{
			return text;
		}
		return text.Insert(lineStart, insertion);
	}

	private static (int LineStart, int LineEnd) FindLabelLine(string text, string label)
	{
		int lineStart = 0;
		while (lineStart < text.Length)
		{
			int lineEnd = text.IndexOf('\n', lineStart);
			if (lineEnd < 0)
			{
				lineEnd = text.Length;
			}

			ReadOnlySpan<char> line = text.AsSpan(lineStart, lineEnd - lineStart).Trim();
			if (line.Equals(label.AsSpan(), StringComparison.OrdinalIgnoreCase))
			{
				return (lineStart, lineEnd);
			}

			lineStart = lineEnd + 1;
		}

		return (-1, -1);
	}

	private static string RemoveBetween(string text, string startMarker, string endMarker, string replacement)
	{
		int start = text.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
		if (start < 0)
		{
			return text;
		}

		int end = text.IndexOf(endMarker, start, StringComparison.OrdinalIgnoreCase);
		if (end < 0)
		{
			return text;
		}

		return text[..start] + replacement + text[end..];
	}

	private static string PortableWinSdkDirHelper()
	{
		return """

:GetPortableWinSdkDir
@REM Use Portable Windows SDK。
set "WindowsSdkDir=%VSINSTALLDIR%..\Windows Kits\10\"
if not exist "%WindowsSdkDir%include\" exit /B 1

setlocal enableDelayedExpansion
set SDK=
set findSDK=
set __check_file=winsdkver.h
if /I "%VSCMD_ARG_APP_PLAT%"=="UWP" set __check_file=Windows.h

for /f %%i IN ('dir "%WindowsSdkDir%include\" /b /ad-h /on') DO (
    if EXIST "%WindowsSdkDir%include\%%i\um\%__check_file%" (
        set SDK=%%i
        if "%%i"=="%VSCMD_ARG_WINSDK%" set findSDK=1
    )
)

if "%findSDK%"=="1" set SDK=%VSCMD_ARG_WINSDK%
endlocal & set WindowsSDKVersion=%SDK%\

if not "%VSCMD_ARG_WINSDK%"=="" (
  if not "%VSCMD_ARG_WINSDK%\"=="%WindowsSDKVersion%" (
    set WindowsSDKVersion=%VSCMD_ARG_WINSDK%\
    set WindowsSDKNotFound=1
  )
) else (
  if "%WindowsSDKVersion%"=="\" (
    set WindowsSDKNotFound=1
    set WindowsSdkDir=
    set WindowsSDKBinPath=
    set WindowsSDKVerBinPath=
    goto :GetPortableWinSdkDirExit
  )
)

if not "%WindowsSDKVersion%"=="\" set WindowsSDKLibVersion=%WindowsSDKVersion%
set "WindowsSdkBinPath=%WindowsSDKDir%bin\"
if EXIST "%WindowsSDKDir%bin\%WindowsSDKVersion%" (
    set "WindowsSdkVerBinPath=%WindowsSDKDir%bin\%WindowsSDKVersion%"
)

set _WinSdkVer_tmp=%WindowsSDKVersion:~0,-1%
if EXIST "%WindowsSdkDir%UnionMetadata\%_WinSdkVer_tmp%" (
  set "WindowsLibPath=%WindowsSdkDir%UnionMetadata\%_WinSdkVer_tmp%;%WindowsSdkDir%References\%_WinSdkVer_tmp%"
) else (
  set "WindowsLibPath=%WindowsSdkDir%UnionMetadata;%WindowsSdkDir%References"
)
set _WinSdkVer_tmp=

:GetPortableWinSdkDirExit
if "%WindowsSDKNotFound%"=="1" (
  set WindowsSDKNotFound=
  exit /B 1
)
exit /B 0

""";
	}

	private static string PortableUniversalCrtSdkDirHelper()
	{
		return """

:GetPortableUniversalCRTSdkDir
@REM Use Portable Universal CRT SDK。
set "UniversalCRTSdkDir=%VSINSTALLDIR%..\Windows Kits\10\"
if not exist "%UniversalCRTSdkDir%include\" exit /B 1

setlocal enableDelayedExpansion
set CRT=
set match=
for /f %%i IN ('dir "%UniversalCRTSdkDir%include\" /b /ad-h /on') DO (
    if EXIST "%UniversalCRTSdkDir%include\%%i\ucrt\corecrt.h" (
        set CRT=%%i
        if "%%i"=="%VSCMD_ARG_WINSDK%" set match=1
    )
)
if "%match%"=="1" set CRT=%VSCMD_ARG_WINSDK%
endlocal & set UCRTVersion=%CRT%\

if "%UCRTVersion%"=="\" (
  set UniversalCRTSdkDir=
  exit /B 1
)
exit /B 0

""";
	}

	private static string ProductLineVersion(InstallPlan plan)
	{
		string? productLine = plan.ProductVersion.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
		if (productLine != null)
		{
			return productLine;
		}

		return plan.Vs switch
		{
			"2026" => "18",
			"2022" => "17",
			"2019" => "16",
			_ => plan.Vs,
		};
	}
}
