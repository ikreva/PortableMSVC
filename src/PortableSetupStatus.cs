namespace PortableMSVC;

public sealed class PortableSetupStatus
{
	public int SchemaVersion { get; set; } = 1;

	public string PortableRoot { get; set; } = "";

	public string PortableInstaller { get; set; } = "";

	public string InstallerTarget { get; set; } = "";

	public string InstallerAction { get; set; } = "";

	public string InstallerBackup { get; set; } = "";

	public string InstallerPreviousJunctionTarget { get; set; } = "";

	public string WindowsSdkRoot { get; set; } = "";

	public List<PortableRegistryViewBackup> RegistryBackups { get; set; } = new();

	public string CreatedAtUtc { get; set; } = "";
}

public sealed class PortableRegistryViewBackup
{
	public string View { get; set; } = "";

	public PortableRegistryValueBackup InstallationFolder { get; set; } = new();

	public PortableRegistryValueBackup KitsRoot10 { get; set; } = new();
}

public sealed class PortableRegistryValueBackup
{
	public string KeyPath { get; set; } = "";

	public string ValueName { get; set; } = "";

	public bool HadValue { get; set; }

	public string OldValue { get; set; } = "";
}
