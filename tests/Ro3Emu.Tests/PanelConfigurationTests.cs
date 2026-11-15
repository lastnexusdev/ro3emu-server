using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using static Ro3Emu.Tests.PanelTests;

namespace Ro3Emu.Tests;

public sealed class PanelConfigurationTests : IAsyncLifetime
{
    private const string Admin = "config-admin", Moderator = "config-mod", Endpoint = "/api/admin/server/config";
    private SyntheticHost _host = null!;
    private string _panel = "";

    public async Task InitializeAsync() => (_host, _panel) = await StartAsync(o =>
    {
        o.GmAccounts = [Admin];
        o.Panel.Roles[Moderator] = "moderator";
        o.OperatorToken = "private-config-token-for-testing-12345678";
        o.Feedback.DiscordWebhookUrl = "https://example.invalid/private-webhook";
    });

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private async Task<PanelBrowser> SignInAsync(string account)
    {
        Assert.Equal(0, await SdkLoginAsync(_host, account, "pw"));
        var browser = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.OK, (await browser.LoginAsync(account, "pw")).StatusCode);
        return browser;
    }

    private static JsonElement Setting(JsonElement config, string path) =>
        config.GetProperty("settings").EnumerateArray().Single(s => s.GetProperty("path").GetString() == path);

    private static async Task<JsonElement> SaveAsync(PanelBrowser browser, JsonElement config, object changes)
    {
        var response = await browser.PostAsync(Endpoint, new { revision = config.GetProperty("revision").GetString(), changes });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("configuration");
    }

    [Fact]
    public async Task ConfigurationIsAdminOnlyAndSecretsAreNeverReturned()
    {
        using var anonymous = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Http.GetAsync(Endpoint)).StatusCode);
        using var moderator = await SignInAsync(Moderator);
        Assert.Equal(HttpStatusCode.Forbidden, (await moderator.Http.GetAsync(Endpoint)).StatusCode);
        using var admin = await SignInAsync(Admin);
        var config = await admin.GetJsonAsync(Endpoint);
        Assert.DoesNotContain("private-config-token", config.GetRawText());
        Assert.DoesNotContain("private-webhook", config.GetRawText());
        Assert.True(Setting(config, "OperatorToken").GetProperty("secret").GetBoolean());
        Assert.True(config.GetProperty("settings").GetArrayLength() > 100);
        Assert.True(Setting(config, "Experience.MonsterPercent").GetProperty("live").GetBoolean());
        Assert.False(Setting(config, "HttpPort").GetProperty("live").GetBoolean());
        admin.Csrf = "wrong";
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync(Endpoint,
            new { revision = config.GetProperty("revision").GetString(), changes = new { ClusterName = "refused" } })).StatusCode);
    }

    [Fact]
    public async Task LiveRatesChangeWithoutRestartAndSavedSettingsSurviveRestart()
    {
        using var admin = await SignInAsync(Admin);
        var before = await admin.GetJsonAsync(Endpoint);
        var config = await SaveAsync(admin, before, new Dictionary<string, object?>
        {
            ["Experience.MonsterPercent"] = 200, ["Experience.JobFromBasePercent"] = 50,
            ["Experience.CatchUpEnabled"] = false, ["ClusterName"] = "Configured realm",
            ["FieldExpRates"] = new[] { new FieldExpRule { MinLevel = 1, MaxLevel = 99, Percent = 300 } },
        });
        Assert.Equal(200, _host.Service<CharacterService>().Experience.MonsterPercent);
        Assert.Equal(50, _host.Service<CharacterService>().JobExpFromBase(100));
        Assert.Equal(100, _host.Service<CharacterService>().WithPursueBonus(1, 100));
        Assert.Equal(300, Assert.Single(_host.Service<RewardService>().FieldExpRules).Percent);
        Assert.Equal("RO3Emu", Setting(config, "ClusterName").GetProperty("active").GetString());
        Assert.True(Setting(config, "ClusterName").GetProperty("pendingRestart").GetBoolean());
        var conflict = await admin.PostAsync(Endpoint, new { revision = before.GetProperty("revision").GetString(), changes = new { ClusterName = "stale" } });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var audit = await admin.GetJsonAsync("/api/admin/audit");
        Assert.Contains(audit.GetProperty("entries").EnumerateArray(), e => e.GetProperty("action").GetString() == "server.config");
        await _host.RestartAsync();
        using var afterRestart = await SignInAsync(Admin);
        var loaded = await afterRestart.GetJsonAsync(Endpoint);
        Assert.Equal("Configured realm", Setting(loaded, "ClusterName").GetProperty("active").GetString());
        Assert.False(Setting(loaded, "ClusterName").GetProperty("pendingRestart").GetBoolean());
        Assert.Equal(50, _host.Service<CharacterService>().JobExpFromBase(100));
        _host.Service<RewardService>().SetFieldExpRules([]);
        _host.Service<RewardService>().ResetFieldExpRules();
        Assert.Equal(300, Assert.Single(_host.Service<RewardService>().FieldExpRules).Percent);
    }

    [Theory]
    [InlineData("Experience.MonsterPercent", "-1")]
    [InlineData("Experience.JobFromBasePercent", "10001")]
    [InlineData("HttpPort", "0")]
    [InlineData("ClusterName", "null")]
    [InlineData("DataDir", "\"\\u0000\"")]
    [InlineData("HttpPort", "5381")]
    [InlineData("NotASetting", "1")]
    [InlineData("FieldExpRates", "[{\"MinLevel\":30,\"MaxLevel\":1,\"Percent\":200}]")]
    [InlineData("FieldExpRates", "[null]")]
    [InlineData("Experience.CatchUpEnabled", "null")]
    [InlineData("Voice", "{}")]
    public async Task InvalidBatchCannotChangeAnySavedOrLiveSetting(string path, string json)
    {
        using var admin = await SignInAsync(Admin);
        var config = await admin.GetJsonAsync(Endpoint);
        var changes = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["Experience.MonsterPercent"] = JsonSerializer.SerializeToElement(200),
            [path] = JsonDocument.Parse(json).RootElement.Clone(),
        };
        // The panel port is dynamically assigned in tests, so make this collision deterministic.
        if (path == "HttpPort" && json == "5381") changes[path] = Setting(config, "Panel.Port").GetProperty("active").Clone();
        var response = await admin.PostAsync(Endpoint, new { revision = config.GetProperty("revision").GetString(), changes });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(100, _host.Service<CharacterService>().Experience.MonsterPercent);
        Assert.Equal(config.GetProperty("revision").GetString(), (await admin.GetJsonAsync(Endpoint)).GetProperty("revision").GetString());
        Assert.False(File.Exists(Path.Combine(_host.DataDir, "server-config.json")));
    }

    [Fact]
    public async Task FutureExpGrantsUseTheSavedJobRateWithoutChangingExistingProgress()
    {
        using var admin = await SignInAsync(Admin);
        await using var bot = await _host.InWorldAsync("ConfigGain");
        var session = _host.Service<SessionRegistry>().All().Single(s => s.Account == "configgain" && s.Role != null);
        var characters = _host.Service<CharacterService>();
        long previousBase = session.Progress!.Exp, previousJob = session.Progress.JobExp;
        var config = await admin.GetJsonAsync(Endpoint);
        await SaveAsync(admin, config, new Dictionary<string, object?>
        {
            ["Experience.JobFromBasePercent"] = 50, ["Experience.CatchUpEnabled"] = false,
        });
        Assert.Equal(previousBase, session.Progress.Exp);
        Assert.Equal(previousJob, session.Progress.JobExp);
        var gains = new List<(long Base, long Job)>();
        characters.ExpCredited.Add((_, baseExp, jobExp) => gains.Add((baseExp, jobExp)));
        await _host.Service<GameLoop>().InvokeAsync(() => characters.AddExpAsync(session, 100, 0));
        Assert.Equal((100L, 50L), Assert.Single(gains));
    }

    [Fact]
    public async Task AutomaticDefaultsAndEquivalentPathsDoNotStayPendingAfterRestart()
    {
        using var admin = await SignInAsync(Admin);
        var config = await admin.GetJsonAsync(Endpoint);
        var saved = await SaveAsync(admin, config, new Dictionary<string, object?>
        {
            ["GmOpenWhenEmpty"] = null, ["Panel.RequireAdmin2FA"] = null, ["DataDir"] = Path.Combine(_host.DataDir, "."),
        });
        foreach (string path in new[] { "GmOpenWhenEmpty", "Panel.RequireAdmin2FA", "DataDir" })
            Assert.False(Setting(saved, path).GetProperty("pendingRestart").GetBoolean());
        await _host.RestartAsync();
        using var afterRestart = await SignInAsync(Admin);
        var loaded = await afterRestart.GetJsonAsync(Endpoint);
        foreach (string path in new[] { "GmOpenWhenEmpty", "Panel.RequireAdmin2FA", "DataDir" })
            Assert.False(Setting(loaded, path).GetProperty("pendingRestart").GetBoolean());
    }

    [Fact]
    public async Task FailedFileWriteLeavesRuntimeUnchanged()
    {
        using var admin = await SignInAsync(Admin);
        var config = await admin.GetJsonAsync(Endpoint);
        Directory.CreateDirectory(Path.Combine(_host.DataDir, "server-config.json"));
        var response = await admin.PostAsync(Endpoint, new
        {
            revision = config.GetProperty("revision").GetString(),
            changes = new Dictionary<string, object> { ["Experience.MonsterPercent"] = 300 }
        });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(100, _host.Service<CharacterService>().Experience.MonsterPercent);
    }
}
