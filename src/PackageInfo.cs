using System.Collections.Generic;

namespace PortableMSVC;

public sealed record PackageInfo(
	string Id,
	string IdLower,
	string? Version,
	string? Language,
	IReadOnlyList<string> Dependencies,
	IReadOnlyList<PayloadInfo> Payloads,
	string? Type = null,
	string? Chip = null,
	string? MachineArch = null,
	string? ProductArch = null);
