using Apex.Core;

namespace Apex.Services;

/// The chosen cloud provider and its key, as on the Mac. The key lives in
/// Credential Manager; the provider, address and model in Apex's settings.
internal static class CloudKeys
{
    const string Target = "Apex Activity Manager: Cloud AI";

    public static CloudProvider Provider
    {
        get => Enum.TryParse<CloudProvider>(Settings.Get("cloudProvider", ""), out var p) ? p : CloudProvider.OpenAI;
        set => Settings.Set("cloudProvider", value.ToString());
    }
    public static string CustomBase { get => Settings.Get("cloudBase", ""); set => Settings.Set("cloudBase", value.Trim()); }
    public static string Base => (CloudProviders.Base(Provider) ?? CustomBase).TrimEnd('/');
    public static string Model { get => Settings.Get("cloudModel", ""); set => Settings.Set("cloudModel", value); }
    public static string? Key => LumenAccount.Read(Target);
    public static bool Ready => Key != null && Base.Length > 0;

    public static bool Save(string key) => LumenAccount.Write(key.Trim(), Target);
    public static void Forget() { LumenAccount.Delete(Target); Model = ""; }
}
