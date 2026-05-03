using System.Text.Encodings.Web;
using System.Text.Json;

namespace PortableMSVC;

public sealed partial class InstallRunner
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
