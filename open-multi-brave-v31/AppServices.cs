namespace OpenMultiBraveLauncherV3;

internal static class AppServices
{
    public static readonly HttpClient Http = new();

    public static readonly HttpClient DirectHttp = new(new HttpClientHandler
    {
        UseProxy = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
}
