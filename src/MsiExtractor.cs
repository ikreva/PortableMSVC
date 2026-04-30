using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace PortableMSVC;

public sealed class MsiExtractor
{
	private sealed record DirectoryRow(string Id, string? Parent, string DefaultDir);

	private sealed record FileRow(string Id, string Component, string FileName, int Sequence);

	private sealed record MediaRow(int DiskId, int LastSequence, string Cabinet);

	public IReadOnlyList<string> GetCabinetNames(string msiPath)
	{
		nint database = Open(msiPath);
		try
		{
			return (from x in Query(database, "SELECT `Cabinet` FROM `Media`")
				select x[0] into x
				where !string.IsNullOrWhiteSpace(x)
				select x.TrimStart('#')).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
		}
		finally
		{
			MsiNative.MsiCloseHandle(database);
		}
	}

	public void Extract(string msiPath, string outputDirectory, string installerDirectory)
	{
		nint database = Open(msiPath);
		try
		{
			// MSI 表描述 CAB 内文件应落到哪里。这里直接重建路径映射。
			Dictionary<string, DirectoryRow> directories = (from x in Query(database, "SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`")
				select new DirectoryRow(x[0], EmptyToNull(x[1]), x[2])).ToDictionary<DirectoryRow, string>((DirectoryRow x) => x.Id, StringComparer.OrdinalIgnoreCase);
			Dictionary<string, string> components = Query(database, "SELECT `Component`, `Directory_` FROM `Component`").ToDictionary<string[], string, string>((string[] x) => x[0], (string[] x) => x[1], StringComparer.OrdinalIgnoreCase);
			List<FileRow> files = (from x in Query(database, "SELECT `File`, `Component_`, `FileName`, `Sequence` FROM `File`")
				select new FileRow(x[0], x[1], LongName(x[2]), int.Parse(x[3]))).ToList();
			List<MediaRow> media = (from x in Query(database, "SELECT `DiskId`, `LastSequence`, `Cabinet` FROM `Media`")
				select new MediaRow(int.Parse(x[0]), int.Parse(x[1]), x[2]) into x
				orderby x.LastSequence
				select x).ToList();
			Dictionary<string, string> directoryPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, string> fileTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (FileRow file in files)
			{
				if (components.TryGetValue(file.Component, out var directoryId))
				{
					string relativeDirectory = ResolveDirectory(directoryId);
					string target = SafeOutputPath(outputDirectory, relativeDirectory, file.FileName);
					fileTargets[file.Id] = target;
				}
			}
			foreach (IGrouping<MediaRow?, FileRow> group in from fileRow in files
				group fileRow by FindMedia(media, fileRow.Sequence))
			{
				MediaRow? mediaRow = group.Key;
				if (mediaRow is null || string.IsNullOrWhiteSpace(mediaRow.Cabinet))
				{
					continue;
				}
				string cabinetName = mediaRow.Cabinet.TrimStart('#');
				string cabinetPath = Path.Combine(installerDirectory, cabinetName);
				if (!File.Exists(cabinetPath))
				{
					if (!mediaRow.Cabinet.StartsWith('#'))
					{
						throw new FileNotFoundException("CAB 文件不存在（MSI: " + Path.GetFileName(msiPath) + "）: " + cabinetPath, cabinetPath);
					}
					ExtractEmbeddedCabinet(database, cabinetName, cabinetPath);
				}
				Dictionary<string, string> groupTargets = group.Where((FileRow fileRow) => fileTargets.ContainsKey(fileRow.Id)).ToDictionary<FileRow, string, string>((FileRow fileRow) => fileRow.Id, (FileRow fileRow) => fileTargets[fileRow.Id], StringComparer.OrdinalIgnoreCase);
				ExtractCabinet(cabinetPath, groupTargets);
			}
			string ResolveDirectory(string id)
			{
				if (directoryPaths.TryGetValue(id, out string? cached))
				{
					return cached;
				}
				if (!directories.TryGetValue(id, out var row))
				{
					return "";
				}
				string segment = DirectorySegment(row.DefaultDir);
				string parent = ((row.Parent == null) ? "" : ResolveDirectory(row.Parent));
				string value = (string.IsNullOrEmpty(parent) ? segment : Path.Combine(parent, segment));
				directoryPaths[id] = value;
				return value;
			}
		}
		finally
		{
			MsiNative.MsiCloseHandle(database);
		}
	}

	private static void ExtractEmbeddedCabinet(nint database, string cabinetName, string destination)
	{
		if (TryExtractStream(database, "_Streams", cabinetName, destination) || TryExtractStream(database, "Binary", cabinetName, destination) || TryExtractStream(database, "Binary", Path.GetFileNameWithoutExtension(cabinetName), destination))
		{
			return;
		}
		throw new FileNotFoundException("MSI 内部 stream 中未找到内嵌 CAB: " + cabinetName);
	}

	private static bool TryExtractStream(nint database, string tableName, string streamName, string destination)
	{
		string escaped = streamName.Replace("'", "''", StringComparison.Ordinal);
		string sql = $"SELECT `Data` FROM `{tableName}` WHERE `Name`='{escaped}'";
		if (MsiNative.MsiDatabaseOpenViewW(database, sql, out var view) != 0)
		{
			return false;
		}
		try
		{
			if (MsiNative.MsiViewExecute(view, 0) != 0)
			{
				return false;
			}
			if (MsiNative.MsiViewFetch(view, out var record) != 0)
			{
				return false;
			}
			try
			{
				string? destinationDirectory = Path.GetDirectoryName(destination);
				if (destinationDirectory != null)
				{
					Directory.CreateDirectory(destinationDirectory);
				}
				using FileStream output = File.Create(destination);
				byte[] buffer = new byte[65536];
				while (true)
				{
					uint size = (uint)buffer.Length;
					int result = MsiNative.MsiRecordReadStream(record, 1u, buffer, ref size);
					if (result != 0)
					{
						throw new InvalidOperationException($"MsiRecordReadStream 失败（错误码 {result}）: {tableName}/{streamName}");
					}
					if (size == 0)
					{
						break;
					}
					output.Write(buffer, 0, (int)size);
				}
			}
			finally
			{
				MsiNative.MsiCloseHandle(record);
			}
			return true;
		}
		finally
		{
			MsiNative.MsiCloseHandle(view);
		}
	}

	private static void ExtractCabinet(string cabinetPath, Dictionary<string, string> fileTargets)
	{
		CabNative.CabinetCallback callback = (_, notification, param1, _) =>
		{
			if (notification != 17)
			{
				return 0u;
			}
			CabNative.FileInCabinetInfo structure = Marshal.PtrToStructure<CabNative.FileInCabinetInfo>((nint)param1);
			string cabinetFileName = Marshal.PtrToStringUni(structure.NameInCabinet) ?? "";
			string? fileNameWithoutExtension = Path.GetFileNameWithoutExtension(cabinetFileName);
			if (!fileTargets.TryGetValue(cabinetFileName, out string? value) && (fileNameWithoutExtension == null || !fileTargets.TryGetValue(fileNameWithoutExtension, out value)))
			{
				return 2u;
			}
			string? targetDirectory = Path.GetDirectoryName(value);
			if (targetDirectory != null)
			{
				Directory.CreateDirectory(targetDirectory);
			}
			structure.FullTargetName = value;
			Marshal.StructureToPtr(structure, (nint)param1, fDeleteOld: false);
			return 1u;
		};
		if (!CabNative.SetupIterateCabinetW(cabinetPath, 0u, callback, 0))
		{
			int error = Marshal.GetLastWin32Error();
			throw new InvalidOperationException($"SetupIterateCabinetW 失败（{error}: {new Win32Exception(error).Message}）: {cabinetPath}");
		}
	}

	private static nint Open(string msiPath)
	{
		nint database;
		int result = MsiNative.MsiOpenDatabaseW(msiPath, 0, out database);
		if (result != 0)
		{
			throw new InvalidOperationException($"MsiOpenDatabase 失败（错误码 {result}）: {msiPath}");
		}
		return database;
	}

	private static List<string[]> Query(nint database, string sql)
	{
		int result = MsiNative.MsiDatabaseOpenViewW(database, sql, out var view);
		if (result != 0)
		{
			throw new InvalidOperationException($"MsiDatabaseOpenView 失败（错误码 {result}）: {sql}");
		}
		try
		{
			result = MsiNative.MsiViewExecute(view, 0);
			if (result != 0)
			{
				throw new InvalidOperationException($"MsiViewExecute 失败（错误码 {result}）: {sql}");
			}
			List<string[]> rows = new List<string[]>();
			nint record;
			while (MsiNative.MsiViewFetch(view, out record) == 0)
			{
				try
				{
					uint columns = MsiNative.MsiRecordGetFieldCount(record);
					string[] row = new string[columns];
					for (uint i = 1u; i <= columns; i++)
					{
						int integer = MsiNative.MsiRecordGetInteger(record, i);
						row[i - 1] = ((integer == int.MinValue) ? MsiNative.GetString(record, i) : integer.ToString());
					}
					rows.Add(row);
				}
				finally
				{
					MsiNative.MsiCloseHandle(record);
				}
			}
			return rows;
		}
		finally
		{
			MsiNative.MsiCloseHandle(view);
		}
	}

	private static MediaRow? FindMedia(IReadOnlyList<MediaRow> media, int sequence)
	{
		return media.FirstOrDefault((MediaRow x) => sequence <= x.LastSequence);
	}

	private static string LongName(string value)
	{
		int index = value.IndexOf('|');
		return index < 0 ? value : value[(index + 1)..];
	}

	private static string DirectorySegment(string value)
	{
		// DefaultDir 格式：[ShortName|]LongName[:SourceName]
		// 先取 | 后的 long name，再取 : 前的 target name（安装目标路径）。
		string longName = LongName(value);
		if (longName is "." or "TARGETDIR" or "SourceDir")
		{
			return "";
		}

		int colon = longName.IndexOf(':');
		return colon < 0 ? longName : longName[..colon];
	}

	private static string? EmptyToNull(string value)
	{
		return string.IsNullOrWhiteSpace(value) ? null : value;
	}

	private static string SafeOutputPath(string outputDirectory, string relativeDirectory, string fileName)
	{
		string root = EnsureTrailingSeparator(Path.GetFullPath(outputDirectory));
		string target = Path.GetFullPath(Path.Combine(root, relativeDirectory, fileName));
		if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("MSI 文件目标路径非法（路径遍历）: " + Path.Combine(relativeDirectory, fileName));
		}
		return target;
	}

	private static string EnsureTrailingSeparator(string path)
	{
		return Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
	}
}
