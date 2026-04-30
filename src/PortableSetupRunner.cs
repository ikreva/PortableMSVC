using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace PortableMSVC;

public static class PortableSetupRunner
{
	private const string ProbeMarker = "PortableMSVCFakeVsWhere";
	private const string InstallerActionCreated = "created";
	private const string InstallerActionRenamedDirectory = "renamed-directory";
	private const string InstallerActionReplacedJunction = "replaced-junction";
	private const string SdkKeyPath = @"SOFTWARE\Microsoft\Microsoft SDKs\Windows\v10.0";
	private const string KitsKeyPath = @"SOFTWARE\Microsoft\Windows Kits\Installed Roots";
	public static bool IsSetupCommand(string[] args)
	{
		return HasArg(args, "--setup") || HasArg(args, "--clean") || HasArg(args, "--portable-msvc-probe");
	}

	public static int Run(string[] args)
	{
		try
		{
			if (HasArg(args, "--portable-msvc-probe"))
			{
				Console.WriteLine(ProbeMarker);
				return 0;
			}

			if (!OperatingSystem.IsWindows())
			{
				Console.Error.WriteLine("portable setup/clean 仅支持 Windows。");
				return 1;
			}

			bool force = HasArg(args, "--force");
			if (HasArg(args, "--setup"))
			{
				return RunElevatedIfNeeded(args) ?? Setup(force);
			}

			if (HasArg(args, "--clean"))
			{
				return RunElevatedIfNeeded(args) ?? Clean(force);
			}

			return 1;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or JsonException)
		{
			Console.Error.WriteLine(ex.Message);
			return 1;
		}
	}

	[SupportedOSPlatform("windows")]
	private static int Setup(bool force)
	{
		SetupContext context = SetupContext.Create();
		PortableSetupStatus status = LoadStatus(context.StatusPath) ?? CreateStatus(context);
		status.PortableRoot = context.PortableRoot;
		status.PortableInstaller = context.PortableInstaller;
		status.InstallerTarget = context.InstallerTarget;
		status.WindowsSdkRoot = context.WindowsSdkRoot;
		status.CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

		try
		{
			SetupInstallerJunction(context, status, force);
			SetupRegistry(context, status);
			SaveStatus(context.StatusPath, status);
			Console.WriteLine("Registered portable vswhere:");
			Console.WriteLine(context.InstallerTarget + " -> " + context.PortableInstaller);
			Console.WriteLine("Registered portable Windows SDK:");
			Console.WriteLine(context.WindowsSdkRoot);
			return 0;
		}
		catch
		{
			RestoreRegistry(context, status, restoreEvenIfChanged: true);
			RestoreInstaller(context, status, force: true);
			throw;
		}
	}

	[SupportedOSPlatform("windows")]
	private static int Clean(bool force)
	{
		SetupContext context = SetupContext.Create();
		PortableSetupStatus status = LoadStatus(context.StatusPath) ?? CreateStatus(context);
		RestoreRegistry(context, status, restoreEvenIfChanged: false);
		RestoreInstaller(context, status, force);
		if (File.Exists(context.StatusPath))
		{
			File.Delete(context.StatusPath);
		}
		Console.WriteLine("Unregistered portable vswhere:");
		Console.WriteLine(context.InstallerTarget);
		Console.WriteLine("Restored portable Windows SDK registry values when they still belonged to this toolchain.");
		return 0;
	}

	private static void SetupInstallerJunction(SetupContext context, PortableSetupStatus status, bool force)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(context.InstallerTarget) ?? context.VisualStudioProgramFilesRoot);
		if (!Directory.Exists(context.InstallerTarget))
		{
			CreateJunction(context.InstallerTarget, context.PortableInstaller);
			status.InstallerAction = InstallerActionCreated;
			return;
		}

		if (IsDirectoryLink(context.InstallerTarget))
		{
			string? currentTarget = ResolveDirectoryTarget(context.InstallerTarget);
			if (PathEquals(currentTarget, context.PortableInstaller))
			{
				if (string.IsNullOrEmpty(status.InstallerAction))
				{
					status.InstallerAction = InstallerActionCreated;
				}
				return;
			}

			if (!force)
			{
				throw new InvalidOperationException("Visual Studio Installer 路径已是其它 junction。使用 --setup --force 才能接管。");
			}

			status.InstallerPreviousJunctionTarget = currentTarget ?? "";
			DeleteDirectoryLink(context.InstallerTarget);
			CreateJunction(context.InstallerTarget, context.PortableInstaller);
			status.InstallerAction = InstallerActionReplacedJunction;
			return;
		}

		if (!force)
		{
			throw new InvalidOperationException("Visual Studio Installer 路径已存在。使用 --setup --force 才能备份并接管。");
		}

		string backup = ChooseBackupPath(context.InstallerTarget, force);
		Directory.Move(context.InstallerTarget, backup);
		CreateJunction(context.InstallerTarget, context.PortableInstaller);
		status.InstallerBackup = backup;
		status.InstallerAction = InstallerActionRenamedDirectory;
	}

	private static void RestoreInstaller(SetupContext context, PortableSetupStatus status, bool force)
	{
		if (Directory.Exists(context.InstallerTarget) &&
			IsDirectoryLink(context.InstallerTarget) &&
			PathEquals(ResolveDirectoryTarget(context.InstallerTarget), context.PortableInstaller))
		{
			DeleteDirectoryLink(context.InstallerTarget);
		}
		else if (Directory.Exists(context.InstallerTarget) && !force)
		{
			return;
		}

		if (status.InstallerAction.Equals(InstallerActionRenamedDirectory, StringComparison.OrdinalIgnoreCase) &&
			Directory.Exists(status.InstallerBackup) &&
			!Directory.Exists(context.InstallerTarget))
		{
			Directory.Move(status.InstallerBackup, context.InstallerTarget);
		}
		else if (status.InstallerAction.Equals(InstallerActionReplacedJunction, StringComparison.OrdinalIgnoreCase) &&
			!string.IsNullOrWhiteSpace(status.InstallerPreviousJunctionTarget) &&
			!Directory.Exists(context.InstallerTarget))
		{
			CreateJunction(context.InstallerTarget, status.InstallerPreviousJunctionTarget);
		}
	}

	[SupportedOSPlatform("windows")]
	private static void SetupRegistry(SetupContext context, PortableSetupStatus status)
	{
		foreach (RegistryView view in GetWritableRegistryViews())
		{
			PortableRegistryViewBackup backup = GetOrCreateRegistryBackup(status, view);
			backup.InstallationFolder = SetupRegistryValue(view, SdkKeyPath, "InstallationFolder", context.WindowsSdkRoot, backup.InstallationFolder);
			backup.KitsRoot10 = SetupRegistryValue(view, KitsKeyPath, "KitsRoot10", context.WindowsSdkRoot, backup.KitsRoot10);
		}
	}

	[SupportedOSPlatform("windows")]
	private static PortableRegistryValueBackup SetupRegistryValue(RegistryView view, string keyPath, string valueName, string value, PortableRegistryValueBackup backup)
	{
		using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
		using RegistryKey key = baseKey.CreateSubKey(keyPath, writable: true) ?? throw new InvalidOperationException("无法创建注册表项: " + keyPath);
		string? current = key.GetValue(valueName) as string;
		if (PathEquals(current, value))
		{
			return EnsureBackupMetadata(backup, keyPath, valueName);
		}

		if (string.IsNullOrEmpty(backup.KeyPath))
		{
			backup = new PortableRegistryValueBackup
			{
				KeyPath = keyPath,
				ValueName = valueName,
				HadValue = current != null,
				OldValue = current ?? ""
			};
		}

		key.SetValue(valueName, value, RegistryValueKind.String);
		return backup;
	}

	private static PortableRegistryValueBackup EnsureBackupMetadata(PortableRegistryValueBackup backup, string keyPath, string valueName)
	{
		if (!string.IsNullOrEmpty(backup.KeyPath))
		{
			return backup;
		}

		return new PortableRegistryValueBackup
		{
			KeyPath = keyPath,
			ValueName = valueName,
			HadValue = false,
			OldValue = ""
		};
	}

	[SupportedOSPlatform("windows")]
	private static void RestoreRegistry(SetupContext context, PortableSetupStatus status, bool restoreEvenIfChanged)
	{
		foreach (PortableRegistryViewBackup backup in EnumerateRegistryBackups(status))
		{
			if (!TryParseRegistryView(backup.View, out RegistryView view))
			{
				continue;
			}

			RestoreRegistryValue(view, backup.InstallationFolder, context.WindowsSdkRoot, restoreEvenIfChanged);
			RestoreRegistryValue(view, backup.KitsRoot10, context.WindowsSdkRoot, restoreEvenIfChanged);
		}
	}

	[SupportedOSPlatform("windows")]
	private static void RestoreRegistryValue(RegistryView view, PortableRegistryValueBackup backup, string portableValue, bool restoreEvenIfChanged)
	{
		if (string.IsNullOrWhiteSpace(backup.KeyPath) || string.IsNullOrWhiteSpace(backup.ValueName))
		{
			return;
		}

		using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
		using RegistryKey? key = baseKey.OpenSubKey(backup.KeyPath, writable: true);
		if (key == null)
		{
			return;
		}

		string? current = key.GetValue(backup.ValueName) as string;
		if (!PathEquals(current, portableValue) && !restoreEvenIfChanged)
		{
			return;
		}

		if (backup.HadValue)
		{
			key.SetValue(backup.ValueName, backup.OldValue, RegistryValueKind.String);
		}
		else
		{
			key.DeleteValue(backup.ValueName, throwOnMissingValue: false);
		}
	}

	[SupportedOSPlatform("windows")]
	private static IEnumerable<RegistryView> GetWritableRegistryViews()
	{
		if (!Environment.Is64BitOperatingSystem)
		{
			yield return RegistryView.Registry32;
			yield break;
		}

		yield return RegistryView.Registry32;
		yield return RegistryView.Registry64;
	}

	[SupportedOSPlatform("windows")]
	private static PortableRegistryViewBackup GetOrCreateRegistryBackup(PortableSetupStatus status, RegistryView view)
	{
		string viewName = view.ToString();
		PortableRegistryViewBackup? backup = status.RegistryBackups.FirstOrDefault(x => x.View.Equals(viewName, StringComparison.OrdinalIgnoreCase));
		if (backup != null)
		{
			return backup;
		}

		backup = new PortableRegistryViewBackup { View = viewName };
		status.RegistryBackups.Add(backup);
		return backup;
	}

	[SupportedOSPlatform("windows")]
	private static IEnumerable<PortableRegistryViewBackup> EnumerateRegistryBackups(PortableSetupStatus status)
	{
		return status.RegistryBackups;
	}

	[SupportedOSPlatform("windows")]
	private static bool TryParseRegistryView(string value, out RegistryView view)
	{
		return Enum.TryParse(value, ignoreCase: true, out view) &&
			(view == RegistryView.Registry32 || view == RegistryView.Registry64);
	}

	private static PortableSetupStatus CreateStatus(SetupContext context)
	{
		return new PortableSetupStatus
		{
			PortableRoot = context.PortableRoot,
			PortableInstaller = context.PortableInstaller,
			InstallerTarget = context.InstallerTarget,
			WindowsSdkRoot = context.WindowsSdkRoot,
			CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
		};
	}

	private static PortableSetupStatus? LoadStatus(string path)
	{
		if (!File.Exists(path))
		{
			return null;
		}

		string json = File.ReadAllText(path);
		return JsonSerializer.Deserialize(json, PortableMSVCJsonContext.Default.PortableSetupStatus);
	}

	private static void SaveStatus(string path, PortableSetupStatus status)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
		string json = JsonSerializer.Serialize(status, PortableMSVCJsonContext.Default.PortableSetupStatus);
		File.WriteAllText(path, json);
	}

	[SupportedOSPlatform("windows")]
	private static int? RunElevatedIfNeeded(string[] args)
	{
		if (IsAdministrator())
		{
			return null;
		}

		string? processPath = Environment.ProcessPath;
		if (string.IsNullOrWhiteSpace(processPath))
		{
			throw new InvalidOperationException("当前进程路径不可用，无法自动提权。");
		}

		using Process? process = Process.Start(new ProcessStartInfo
		{
			FileName = processPath,
			Arguments = string.Join(" ", args.Select(QuoteArgument)),
			UseShellExecute = true,
			Verb = "runas"
		});
		if (process == null)
		{
			return 1;
		}

		process.WaitForExit();
		return process.ExitCode;
	}

	[SupportedOSPlatform("windows")]
	private static bool IsAdministrator()
	{
		using WindowsIdentity identity = WindowsIdentity.GetCurrent();
		WindowsPrincipal principal = new WindowsPrincipal(identity);
		return principal.IsInRole(WindowsBuiltInRole.Administrator);
	}

	private static string QuoteArgument(string value)
	{
		return value.Contains(' ') || value.Contains('"')
			? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
			: value;
	}

	private static string ChooseBackupPath(string target, bool force)
	{
		string backup = target + ".PortableMSVCBackup";
		if (!Directory.Exists(backup))
		{
			return backup;
		}

		if (!force)
		{
			throw new InvalidOperationException("备份目录已存在: " + backup + "。使用 --setup --force 可创建新的时间戳备份。");
		}

		return backup + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
	}

	private static void CreateJunction(string linkPath, string targetPath)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(linkPath) ?? ".");
		RunCommand("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"");
	}

	private static void DeleteDirectoryLink(string path)
	{
		RunCommand("cmd.exe", $"/c rmdir \"{path}\"");
	}

	private static void RunCommand(string fileName, string arguments)
	{
		using Process process = Process.Start(new ProcessStartInfo
		{
			FileName = fileName,
			Arguments = arguments,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		}) ?? throw new InvalidOperationException("无法启动进程: " + fileName);
		process.WaitForExit();
		if (process.ExitCode != 0)
		{
			throw new InvalidOperationException(process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd());
		}
	}

	private static bool IsDirectoryLink(string path)
	{
		return Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
	}

	private static string? ResolveDirectoryTarget(string path)
	{
		try
		{
			return new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
		}
		catch
		{
			return null;
		}
	}

	private static bool PathEquals(string? left, string? right)
	{
		if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
		{
			return false;
		}

		return NormalizePath(left).Equals(NormalizePath(right), StringComparison.OrdinalIgnoreCase);
	}

	private static string NormalizePath(string path)
	{
		return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
	}

	private static bool HasArg(string[] args, string name)
	{
		return args.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
	}

	private sealed class SetupContext
	{
		public string PortableRoot { get; init; } = "";
		public string VisualStudioDirectory { get; init; } = "";
		public string PortableInstaller { get; init; } = "";
		public string VisualStudioProgramFilesRoot { get; init; } = "";
		public string InstallerTarget { get; init; } = "";
		public string WindowsSdkRoot { get; init; } = "";
		public string StatusPath { get; init; } = "";

		public static SetupContext Create()
		{
			string portableInstaller = ResolveDirectoryTarget(AppContext.BaseDirectory) ?? Path.GetFullPath(AppContext.BaseDirectory);
			string visualStudioDirectory = Path.GetFullPath(Path.Combine(portableInstaller, ".."));
			string portableRoot = Path.GetFullPath(Path.Combine(visualStudioDirectory, ".."));
			string programFilesRoot = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
			if (string.IsNullOrWhiteSpace(programFilesRoot))
			{
				programFilesRoot = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
			}

			string visualStudioProgramFilesRoot = Path.Combine(programFilesRoot, "Microsoft Visual Studio");
			string installerTarget = Path.Combine(visualStudioProgramFilesRoot, "Installer");
			string windowsSdkRoot = EnsureTrailingSeparator(Path.Combine(portableRoot, "Windows Kits", "10"));
			string statusPath = Path.Combine(visualStudioDirectory, "Setup", "status.json");
			return new SetupContext
			{
				PortableRoot = portableRoot,
				VisualStudioDirectory = visualStudioDirectory,
				PortableInstaller = Path.TrimEndingDirectorySeparator(portableInstaller),
				VisualStudioProgramFilesRoot = visualStudioProgramFilesRoot,
				InstallerTarget = installerTarget,
				WindowsSdkRoot = windowsSdkRoot,
				StatusPath = statusPath
			};
		}

		private static string EnsureTrailingSeparator(string path)
		{
			return Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
		}
	}
}
