namespace JAASM.App.Models;

public sealed class AppSettings
{
    public string? SteamCmdPath { get; set; }
    public string? SteamCmdInstallDirectory { get; set; }
    public string? AsaServerInstallDirectory { get; set; }

    // Legacy single-profile value is retained for settings migration.
    public AsaServerProfile AsaProfile { get; set; } = new();

    public List<AsaServerProfile> AsaProfiles { get; set; } = new();
    public string? ActiveAsaProfileId { get; set; }
    public WebGuiSettings WebGui { get; set; } = new();
}

public sealed class AsaServerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ServerName { get; set; } = "JAASM Server";
    public string Map { get; set; } = "TheIsland_WP";
    public int MaxPlayers { get; set; } = 70;
    public int GamePort { get; set; } = 7777;
    public int QueryPort { get; set; } = 27015;
    public int RconPort { get; set; } = 27020;
    public string ServerPassword { get; set; } = string.Empty;
    public string AdminPassword { get; set; } = string.Empty;
    public string ExtraArguments { get; set; } = string.Empty;
    public List<string> SelectedExtraArguments { get; set; } = new();
}

public sealed class WebGuiSettings
{
    public bool Enabled { get; set; }
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8484;
}
