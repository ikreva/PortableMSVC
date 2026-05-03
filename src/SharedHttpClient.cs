namespace PortableMSVC;

internal static class SharedHttpClient
{
	public static HttpClient Instance { get; } = Create();

	private static HttpClient Create()
	{
		HttpClient client = new()
		{
			Timeout = TimeSpan.FromMinutes(5)
		};
		client.DefaultRequestHeaders.UserAgent.ParseAdd("PortableMSVC/1.0");
		return client;
	}
}
