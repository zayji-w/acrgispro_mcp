using System.Text.Json;
namespace SafeBridge.Native;

internal sealed record BridgeConfig(string runtime_root, string state_root, string output_root, string gp_catalog_path)
{
    public static string ConfigPath => Path.Combine(
        Environment.GetEnvironmentVariable("LOCALAPPDATA")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GIS-Agent", "bridge.config.json");
    public static BridgeConfig Load(string? explicitPath = null)
    {
        var path=explicitPath ?? ConfigPath;
        if (!File.Exists(path)) throw new BridgeException("INVALID_ARGUMENT", "GIS-Agent configuration not found: " + path);
        var result=JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(path)) ?? throw new BridgeException("INVALID_ARGUMENT","Empty configuration");
        foreach (var p in new[]{result.runtime_root,result.state_root,result.output_root,result.gp_catalog_path})
            if(string.IsNullOrWhiteSpace(p)||!Path.IsPathFullyQualified(p))throw new BridgeException("INVALID_ARGUMENT","Invalid configuration path");
        PathPolicy.EnsureNoLinks(result.state_root); PathPolicy.EnsureNoLinks(result.output_root);
        return result;
    }
}
