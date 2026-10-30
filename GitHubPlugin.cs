using System.Text.Json;
using ARI.Common;
using ARI.LLM;

namespace ARI.Plugins.GitHub;

/// <summary>
/// GitHub for Ari, through GitHub's own CLI. The plugin owns signing in (GitHub's device flow: the owner approves a short code on github.com, so
/// nobody pastes a token), and adds the one <c>github</c> tool while it is on. The token itself lives in ARI's GitHubStore, because ARI's git
/// tools use it too for cloning and pushing; the plugin writes it and the rest of ARI reads it.
/// </summary>
public sealed class GitHubPlugin(IPluginHost host) : IPlugin, IToolProvider
{
    private const string Scope = "repo read:org";
    private readonly object gate = new();
    private bool? enabled;

    private string Folder => Path.Combine(host.PluginsFolder, "github");
    private string SettingsFile => Path.Combine(Folder, "settings.json");

    public string Id => "github";
    public string Name => "GitHub";
    public string Description => "Gives Ari your GitHub account for issues, pull requests, releases and Actions, through GitHub's own CLI. You approve a short code on GitHub itself, so she never sees a token.";
    public string Author => "first-party";
    public byte[]? Icon => File.Exists(Path.Combine(Folder, "icon.png")) ? File.ReadAllBytes(Path.Combine(Folder, "icon.png")) : null;
    public IReadOnlyDictionary<string, string> Source { get; } = PluginSource.Embedded(typeof(GitHubPlugin).Assembly);

    public string ToolsDescription => "GitHub: run any gh command as the connected account (issues, pull requests, repos, releases, Actions, or the API).";

    public IReadOnlyList<ProvidedTool> Tools { get; } =
    [
        new("github", "Run a GitHub CLI (gh) command as the connected GitHub account: issues, pull requests, repos, releases, Actions runs, or `api` for anything else.",
            t => t.IsAdminChat && GitHubStore.ResolveToken() is not null && GhCli.ExecutablePath is { } gh ? new GitHubTool(gh, t.FilesystemRoot, t) : null),
    ];

    public IReadOnlyList<PluginSetting> Settings { get; } =
    [
        new("clientId", "OAuth App Client ID", PluginSettingType.Text, "From a GitHub OAuth App with Device Flow switched on (github.com/settings/developers). Not secret; set once.", "e.g. Iv1.0123456789abcdef"),
        new("coAuthor", "Sign commits as co-author", PluginSettingType.Toggle, "Off: Ari's commits are authored by her and you are the committer. On: they're authored by you with her as Co-Authored-By."),
    ];

    public IReadOnlyList<PluginAction> Actions { get; } =
    [
        new("connect", "Connect to GitHub", "Shows a short code to approve at github.com/login/device."),
        new("disconnect", "Disconnect", "Forgets the connected account. The Client ID stays.", Confirm: true),
    ];

    // ── Switching on and off ─────────────────────────────────────────────────

    public bool Enabled
    {
        get
        {
            lock (gate)
            {
                if (enabled is { } e) return e;
                try
                {
                    if (File.Exists(SettingsFile) && JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(SettingsFile)) is { } d && d.TryGetValue("enabled", out JsonElement v))
                        return (enabled = v.ValueKind == JsonValueKind.True).Value;
                }
                catch { }
                return (enabled = GitHubStore.ResolveToken() is not null).Value;   // an account that was already connected keeps working
            }
        }
    }

    public PluginState State { get; private set; } = PluginState.Stopped;

    public string? Status
    {
        get
        {
            GitHubConnectStatus s = GitHubDeviceAuth.Status;
            if (s.State == GitHubConnectState.Pending) return $"Waiting for you to enter {s.UserCode} at {s.VerificationUri}";
            GitHubSettings g = GitHubStore.Get();
            if (!string.IsNullOrEmpty(g.AccessToken)) return Enabled ? "Connected as " + g.Login : "Switched off (connected as " + g.Login + ")";
            return Enabled ? (string.IsNullOrWhiteSpace(g.ClientId) ? "Not connected: add a Client ID" : "Not connected") : "Switched off";
        }
    }

    public Task<string?> StartAsync()
    {
        if (!Enabled || State == PluginState.Running) return Task.FromResult<string?>(null);
        if (GitHubStore.ResolveToken() is not null) _ = GhCli.EnsureInstalledAsync();   // gh backs the github tool
        State = PluginState.Running;
        return Task.FromResult<string?>(null);
    }

    public Task StopAsync() { State = PluginState.Stopped; return Task.CompletedTask; }

    // ── Settings ─────────────────────────────────────────────────────────────

    public IReadOnlyDictionary<string, object?> GetValues()
    {
        GitHubSettings g = GitHubStore.Get();
        return new Dictionary<string, object?> { ["enabled"] = Enabled, ["clientId"] = g.ClientId, ["coAuthor"] = g.CommitAs == CommitIdentity.CoAuthor };
    }

    public string? SetValues(IReadOnlyDictionary<string, JsonElement> changes, bool callerIsMaster)
    {
        if (changes.TryGetValue("clientId", out JsonElement id) && id.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(id.GetString())) return "The Client ID can't be empty.";
        GitHubSettings g = GitHubStore.Get();
        if (changes.TryGetValue("clientId", out id)) g.ClientId = (id.GetString() ?? "").Trim();
        if (changes.TryGetValue("coAuthor", out JsonElement co)) g.CommitAs = co.ValueKind == JsonValueKind.True ? CommitIdentity.CoAuthor : CommitIdentity.Author;
        GitHubStore.Set(g);

        if (changes.TryGetValue("enabled", out JsonElement en))
        {
            lock (gate)
            {
                enabled = en.ValueKind == JsonValueKind.True;
                Directory.CreateDirectory(Folder);
                File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new Dictionary<string, bool> { ["enabled"] = enabled.Value }));
            }
        }
        return null;
    }

    public async Task<string> RunActionAsync(string key)
    {
        switch (key)
        {
            case "connect":
                string clientId = GitHubStore.Get().ClientId;
                if (string.IsNullOrWhiteSpace(clientId)) return "Save a Client ID first.";
                GitHubConnectStatus s = await GitHubDeviceAuth.StartAsync(clientId, Scope);
                return s.State == GitHubConnectState.Error
                    ? s.Error ?? "Couldn't start the connection."
                    : $"Open {s.VerificationUri} and enter the code {s.UserCode}. Ari connects as soon as you approve it.";
            case "disconnect":
                GitHubStore.Disconnect();
                return "Disconnected.";
            default:
                return $"GitHub has no action called {key}.";
        }
    }
}
