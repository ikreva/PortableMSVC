namespace PortableMSVC;

public sealed partial class InstallRunner
{
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
}
