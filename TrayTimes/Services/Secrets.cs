using Microsoft.Windows.ApplicationModel.Resources;

namespace TrayTimes.Services;

/// <summary>
/// Secrets compiled into the app's resources from the gitignored OAuth.resw
/// (copy OAuth.resw.sample to create it).
/// </summary>
internal static class Secrets
{
    private static readonly Lazy<string?> s_mctsApiKey = new(() => GetOAuthString("MctsApiKey"));
    private static readonly Lazy<string?> s_azureMapsKey = new(() => GetOAuthString("AzureMapsKey"));

    /// <summary>The MCTS BusTime Developer API key, or null when OAuth.resw doesn't provide one.</summary>
    public static string? MctsApiKey => s_mctsApiKey.Value;

    /// <summary>The Azure Maps subscription key for the basemap, or null when OAuth.resw doesn't provide one.</summary>
    public static string? AzureMapsKey => s_azureMapsKey.Value;

    private static string? GetOAuthString(string name)
    {
        try
        {
            ResourceLoader loader = new(ResourceLoader.GetDefaultResourceFilePath(), "OAuth");
            string value = loader.GetString(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            // OAuth.resw is missing from this build.
            return null;
        }
    }
}
