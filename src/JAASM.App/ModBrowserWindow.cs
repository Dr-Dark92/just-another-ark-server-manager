using Avalonia.Controls;

namespace JAASM.App;

/// <summary>
/// Hosts CurseForge in Avalonia's native browser dialog rather than embedding a
/// NativeWebView inside another Avalonia Window. This is materially safer on
/// Windows because WebView2 owns its own native dialog lifecycle.
/// </summary>
public sealed class ModBrowserWindow
{
    private const string AsaModsHome =
        "https://www.curseforge.com/ark-survival-ascended/search?class=mods";

    public string? SelectedModId { get; private set; }

    public async Task ShowAsync(Window owner, string? initialSearch = null)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var dialog = new NativeWebDialog
        {
            Title = "JAASM Mod Browser",
            CanUserResize = true,
            ShowFocused = true,
            Source = BuildSearchUrl(initialSearch ?? string.Empty)
        };

        dialog.Closing += (_, _) =>
        {
            completion.TrySetResult();
        };

        dialog.NavigationCompleted += async (_, _) =>
        {
            // Inject a small JAASM action button directly into CurseForge only
            // when the current page exposes an ASA Project ID.
            const string script = """
                (() => {
                    try {
                        const text = document.body ? document.body.innerText : "";
                        const match = text.match(/Project\s*ID\s*:?\s*(\d{4,10})/i);

                        const old = document.getElementById("jaasm-add-mod-button");
                        if (old) old.remove();

                        if (!match) return "NO_MOD";

                        const id = match[1];
                        const button = document.createElement("button");
                        button.id = "jaasm-add-mod-button";
                        button.textContent = "Add Mod " + id + " to JAASM";
                        button.style.position = "fixed";
                        button.style.right = "22px";
                        button.style.bottom = "22px";
                        button.style.zIndex = "2147483647";
                        button.style.padding = "12px 18px";
                        button.style.border = "1px solid #b64dff";
                        button.style.borderRadius = "7px";
                        button.style.background = "#7d2fa8";
                        button.style.color = "white";
                        button.style.fontWeight = "700";
                        button.style.cursor = "pointer";
                        button.style.boxShadow = "0 6px 24px rgba(0,0,0,.45)";
                        button.onclick = () => invokeCSharpAction("JAASM_ADD_MOD:" + id);
                        document.body.appendChild(button);
                        return id;
                    } catch {
                        return "ERROR";
                    }
                })();
                """;

            try
            {
                await dialog.InvokeScript(script);
            }
            catch
            {
                // A page may disallow injection while navigating. The next
                // successful navigation will retry automatically.
            }
        };

        dialog.WebMessageReceived += (_, e) =>
        {
            const string prefix = "JAASM_ADD_MOD:";
            var body = e.Body ?? string.Empty;

            if (!body.StartsWith(prefix, StringComparison.Ordinal))
                return;

            var id = body[prefix.Length..].Trim();

            if (id.Length is < 4 or > 10 || !id.All(char.IsDigit))
                return;

            SelectedModId = id;
            dialog.Close();
        };

        dialog.Show(owner);
        await completion.Task;
    }

    private static Uri BuildSearchUrl(string search)
    {
        var url = AsaModsHome;

        if (!string.IsNullOrWhiteSpace(search))
            url += "&search=" + Uri.EscapeDataString(search.Trim());

        return new Uri(url);
    }
}
