namespace JAASM.App.Models;

public sealed class AppSettings
{
    public string? SteamCmdPath { get; set; }
    public string? SteamCmdInstallDirectory { get; set; }
    public string? AsaServerInstallDirectory { get; set; }
    public AsaServerProfile AsaProfile { get; set; } = new();
    public WebGuiSettings WebGui { get; set; } = new();
}

public sealed class AsaServerProfile
{
    public string ServerName { get; set; } = "JAASM Server";
    public string Map { get; set; } = "TheIsland_WP";
    public int MaxPlayers { get; set; } = 70;
    public int GamePort { get; set; } = 7777;
    public int QueryPort { get; set; } = 27015;
    public int RconPort { get; set; } = 27020;
    public string ServerPassword { get; set; } = string.Empty;
    public string AdminPassword { get; set; } = string.Empty;
    public string ExtraArguments { get; set; } = string.Empty;
}

public sealed class WebGuiSettings
{
    public bool Enabled { get; set; }
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8484;
}
