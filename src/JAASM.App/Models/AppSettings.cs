namespace JAASM.App.Models;

public sealed class AppSettings
{
    public string? SteamCmdPath { get; set; }
    public string? SteamCmdInstallDirectory { get; set; }
    public WebGuiSettings WebGui { get; set; } = new();
}

public sealed class WebGuiSettings
{
    public bool Enabled { get; set; }
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8484;
}
