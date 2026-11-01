namespace FluentCode.Services.Configuration;

/// <summary>Application-owned stores, independent of any page's lifetime.</summary>
internal static class AppServices
{
    internal static SettingsService Settings { get; } = new();
    internal static DpapiSecretStore Secrets { get; } = new();
}
