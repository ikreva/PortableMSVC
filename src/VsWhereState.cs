using System.Text.Json.Serialization;

namespace PortableMSVC;

public sealed class VsWhereState
{
	[JsonPropertyName("installationName")]
	public string InstallationName { get; set; } = "";

	[JsonPropertyName("catalogInfo")]
	public VsWhereCatalogInfo CatalogInfo { get; set; } = new VsWhereCatalogInfo();

	[JsonPropertyName("installationPath")]
	public string InstallationPath { get; set; } = "";

	[JsonPropertyName("launchParams")]
	public VsWhereLaunchParams LaunchParams { get; set; } = new VsWhereLaunchParams();

	[JsonPropertyName("installationVersion")]
	public string InstallationVersion { get; set; } = "";

	[JsonPropertyName("installDate")]
	public string InstallDate { get; set; } = "";

	[JsonPropertyName("seed")]
	public VsWhereSeed Seed { get; set; } = new VsWhereSeed();

	[JsonPropertyName("localizedResources")]
	public List<VsWhereLocalizedResource> LocalizedResources { get; set; } = new List<VsWhereLocalizedResource>();

	[JsonPropertyName("product")]
	public VsWherePackage Product { get; set; } = new VsWherePackage();

	[JsonPropertyName("selectedPackages")]
	public List<VsWherePackage> SelectedPackages { get; set; } = new List<VsWherePackage>();
}

public sealed class VsWhereCatalogInfo
{
	[JsonPropertyName("id")]
	public string Id { get; set; } = "";

	[JsonPropertyName("buildVersion")]
	public string BuildVersion { get; set; } = "";

	[JsonPropertyName("productDisplayVersion")]
	public string ProductDisplayVersion { get; set; } = "";

	[JsonPropertyName("productLine")]
	public string ProductLine { get; set; } = "";

	[JsonPropertyName("productLineVersion")]
	public string ProductLineVersion { get; set; } = "";

	[JsonPropertyName("productName")]
	public string ProductName { get; set; } = "";

	[JsonPropertyName("productSemanticVersion")]
	public string ProductSemanticVersion { get; set; } = "";
}

public sealed class VsWhereLaunchParams
{
	[JsonPropertyName("fileName")]
	public string FileName { get; set; } = "";
}

public sealed class VsWhereSeed
{
	[JsonPropertyName("languages")]
	public List<string> Languages { get; set; } = new List<string>();
}

public sealed class VsWhereLocalizedResource
{
	[JsonPropertyName("language")]
	public string Language { get; set; } = "";

	[JsonPropertyName("title")]
	public string Title { get; set; } = "";

	[JsonPropertyName("description")]
	public string Description { get; set; } = "";

	[JsonPropertyName("license")]
	public string License { get; set; } = "";
}

public sealed class VsWhereInstance
{
	[JsonPropertyName("instanceId")]
	public string InstanceId { get; set; } = "portable";

	[JsonPropertyName("installDate")]
	public string InstallDate { get; set; } = "";

	[JsonPropertyName("installationName")]
	public string InstallationName { get; set; } = "";

	[JsonPropertyName("installationPath")]
	public string InstallationPath { get; set; } = "";

	[JsonPropertyName("installationVersion")]
	public string InstallationVersion { get; set; } = "";

	[JsonPropertyName("displayName")]
	public string DisplayName { get; set; } = "";

	[JsonPropertyName("description")]
	public string Description { get; set; } = "";

	[JsonPropertyName("productId")]
	public string ProductId { get; set; } = "";

	[JsonPropertyName("productPath")]
	public string ProductPath { get; set; } = "";

	[JsonPropertyName("isComplete")]
	public bool IsComplete { get; set; } = true;

	[JsonPropertyName("isLaunchable")]
	public bool IsLaunchable { get; set; } = true;

	[JsonPropertyName("isPrerelease")]
	public bool IsPrerelease { get; set; }

	[JsonPropertyName("isRebootRequired")]
	public bool IsRebootRequired { get; set; }

	[JsonPropertyName("catalog")]
	public VsWhereCatalogInfo Catalog { get; set; } = new VsWhereCatalogInfo();

	[JsonPropertyName("packages")]
	public List<VsWherePackage> Packages { get; set; } = new List<VsWherePackage>();
}
