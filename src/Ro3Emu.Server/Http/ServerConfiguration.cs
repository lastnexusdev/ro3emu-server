using System.Collections;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Ro3Emu.Panel;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Http;

/// <summary>Operator overrides (inferred): durable file, optimistic edits, and explicitly supported live changes.</summary>
public sealed class ServerConfiguration : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly ServerOptions _active;
    private readonly ServerOptions _initial;
    private readonly GameLoop _loop;
    private readonly RewardService _rewards;
    private readonly CharacterService _characters;
    private readonly AccountStore _accounts;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly string _file;
    private readonly bool _explicitProduction;
    private readonly IReadOnlyDictionary<string, string> _descriptions = LoadDescriptions();

    public ServerConfiguration(IOptions<ServerOptions> options, GameLoop loop, RewardService rewards,
        CharacterService characters, AccountStore accounts, TimeProvider clock, IConfiguration configuration)
    {
        _active = options.Value;
        _initial = Clone(_active);
        _file = _active.PanelConfigurationPath;
        _loop = loop;
        _rewards = rewards;
        _characters = characters;
        _accounts = accounts;
        _clock = clock;
        _explicitProduction = ServerHost.ExplicitProduction(configuration);
    }

    private sealed record Setting(string Path, PropertyInfo[] Chain)
    {
        public Type Type => Chain[^1].PropertyType;
        public bool Secret => Path is nameof(ServerOptions.OperatorToken) or "Feedback.DiscordWebhookUrl";
        public bool Nullable => System.Nullable.GetUnderlyingType(Type) != null
            || !Type.IsValueType && new NullabilityInfoContext().Create(Chain[^1]).ReadState != NullabilityState.NotNull;
        public bool Live => Path.StartsWith("Experience.", StringComparison.Ordinal)
            || Path is "FieldExpRates" or "StarCoreDrops" or "TitleCrestPercent" or "TavernDayDrinks" or "RaidPreparationSeconds";
        public object? Get(ServerOptions options) => Chain.Aggregate((object?)options, (value, property) => value == null ? null : property.GetValue(value));
        public void Set(ServerOptions options, JsonElement value)
        {
            object owner = options;
            foreach (var property in Chain[..^1]) owner = property.GetValue(owner) ?? throw new ArgumentException(Path + " has no parent.");
            try { Chain[^1].SetValue(owner, value.Deserialize(Type, Json)); }
            catch (JsonException) { throw new ArgumentException(Path + ": enter a valid " + Kind(Type) + " value."); }
        }
    }

    private static IEnumerable<Setting> Schema(Type type, string prefix = "", PropertyInfo[]? parents = null)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && p.GetIndexParameters().Length == 0))
        {
            string path = prefix + property.Name;
            PropertyInfo[] chain = [.. parents ?? [], property];
            var valueType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (valueType.IsClass && valueType != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(valueType))
                foreach (var child in Schema(valueType, path + ".", chain)) yield return child;
            else yield return new Setting(path, chain);
        }
    }

    private static string Kind(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(bool) ? "boolean" : type == typeof(string) ? "string"
            : type.IsPrimitive || type == typeof(decimal) ? "number" : "json";
    }

    private static ServerOptions Clone(ServerOptions options) =>
        JsonSerializer.Deserialize<ServerOptions>(JsonSerializer.Serialize(options, Json), Json)!;

    private static Dictionary<string, JsonElement> ReadOverrides(string file) => File.Exists(file)
        ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllBytes(file), Json)
            ?? throw new InvalidDataException("The server configuration must be a JSON object.")
        : new(StringComparer.Ordinal);

    private static string Revision(string file) => File.Exists(file)
        ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) : "initial";

    /// <summary>Loaded before services start, relative to the original configured data directory, even when DataDir changes.</summary>
    internal static ServerOptions Load(ServerOptions options)
    {
        string file = Path.Combine(options.DataDir, "server-config.json");
        if (File.Exists(file)) ApplyOverrides(options, ReadOverrides(file));
        options.PanelConfigurationPath = file;
        return options;
    }

    private static void ApplyOverrides(ServerOptions options, IReadOnlyDictionary<string, JsonElement> overrides)
    {
        var schema = Schema(typeof(ServerOptions)).ToDictionary(s => s.Path, StringComparer.Ordinal);
        foreach (var (path, value) in overrides)
        {
            if (!schema.TryGetValue(path, out var setting)) throw new ArgumentException("Unknown setting: " + path);
            CheckValue(setting, value);
            setting.Set(options, value);
        }
        Validate(options);
    }

    private static void CheckValue(Setting setting, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null && !setting.Nullable)
            throw new ArgumentException(setting.Path + " cannot be empty.");
        if (value.ValueKind == JsonValueKind.Null && setting.Type.IsClass && setting.Type != typeof(string))
            throw new ArgumentException(setting.Path + " must contain a JSON collection.");
        if (value.GetRawText().Length > 32768) throw new ArgumentException(setting.Path + " is too large.");
        if (Kind(setting.Type) == "json") CheckCollection(value, setting.Path);
    }

    private static void CheckCollection(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Null) throw new ArgumentException(path + " cannot contain null collection entries.");
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var entry in value.EnumerateArray()) CheckCollection(entry, path);
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var entry in value.EnumerateObject()) CheckCollection(entry.Value, path);
    }

    private static void Validate(ServerOptions options)
    {
        if (!IPAddress.TryParse(options.PublicHost, out _)) throw new ArgumentException("PublicHost must be an IP address.");
        foreach (string? address in new[] { options.BindAddress, options.Panel.BindAddress })
            if (!string.IsNullOrEmpty(address) && !IPAddress.TryParse(address, out _)) throw new ArgumentException("Bind addresses must be IP addresses.");
        int[] ports = [options.HttpPort, options.MicroGatePort, options.GameGatePort,
            .. options.Panel.Enabled ? new[] { options.Panel.Port } : [], .. options.Voice.Port > 0 ? new[] { options.Voice.Port } : []];
        if (ports.Any(p => p is < 1 or > 65535) || ports.Distinct().Count() != ports.Length)
            throw new ArgumentException("Enabled ports must be distinct numbers from 1 to 65535.");
        if (string.IsNullOrWhiteSpace(options.DataDir)) throw new ArgumentException("DataDir cannot be empty.");
        try { _ = ServerHost.AbsoluteDataDir(options.DataDir); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("DataDir must be a valid directory path.");
        }
        if (options.OperatorToken is { } token && (token.Length < 32 || token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))))
            throw new ArgumentException("OperatorToken needs at least 32 characters and no whitespace.");
        if (options.WalkCheck is not ("enforce" or "log" or "off") || options.TravelCheck is not ("enforce" or "log" or "off"))
            throw new ArgumentException("WalkCheck and TravelCheck must be enforce, log or off.");
        ValidateGameplay(options);
        foreach (var setting in Schema(typeof(ServerOptions)))
        {
            if (setting.Path == "Ro1AggressiveMaxLevelGap" || Kind(setting.Type) != "number" || setting.Get(options) is not { } value) continue;
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (!double.IsFinite(number) || number < 0) throw new ArgumentException(setting.Path + " must be a finite nonnegative number.");
        }
    }

    private static void ValidateGameplay(ServerOptions options)
    {
        if (options.Experience.MonsterPercent is < 0 or > 10000 || options.Experience.JobFromBasePercent is < 0 or > 10000)
            throw new ArgumentException("EXP percentages must be from 0 to 10000 (100 = 1x).");
        if (FieldExpRates.Valid(options).Length != options.FieldExpRates.Count)
            throw new ArgumentException("Field EXP rules need MinLevel >= 1, MaxLevel >= MinLevel and Percent from 1 to 10000.");
        if (options.SpawnLimits.Any(r => r.Scene < 0 || r.Npc < 0 || r.Max < 0)) throw new ArgumentException("Spawn limits cannot be negative.");
        if (options.LordsManePercent is < 0 or > 100 || options.TitleCrestPercent is < 0 or > 100)
            throw new ArgumentException("Drop chances must be from 0 to 100 percent.");
        if (options.RaidPreparationSeconds < 0 || options.RaidEnrageSeconds < 0)
            throw new ArgumentException("Raid timers cannot be negative.");
        if (options.Panel.Roles.Values.Any(role => PanelRoles.Parse(role) == null)) throw new ArgumentException("Staff roles must be moderator, gm, admin or none.");
    }

    public Task<PanelServerConfiguration> ReadAsync() => _loop.InvokeAsync(() => Task.FromResult(Snapshot()));

    private PanelServerConfiguration Snapshot()
    {
        var saved = Clone(_initial);
        ApplyOverrides(saved, ReadOverrides(_file));
        var settings = Schema(typeof(ServerOptions)).Select(setting =>
        {
            object? current = setting.Path == "FieldExpRates" ? _rewards.FieldExpRules : setting.Get(_active);
            var active = JsonSerializer.SerializeToElement(current, Json);
            var desired = JsonSerializer.SerializeToElement(setting.Get(saved), Json);
            bool pending = !setting.Live && !JsonElement.DeepEquals(active, EffectiveSaved(setting, saved));
            if (setting.Secret) active = desired = JsonSerializer.SerializeToElement<string?>(null);
            return new PanelConfigSetting(setting.Path, Kind(setting.Type), Help(setting), active, desired, setting.Live, pending, setting.Secret, setting.Nullable);
        }).OrderBy(s => s.Path, StringComparer.Ordinal).ToArray();
        return new PanelServerConfiguration(Revision(_file), settings);
    }

    private JsonElement EffectiveSaved(Setting setting, ServerOptions saved)
    {
        object? value = setting.Path switch
        {
            "DataDir" => ServerHost.AbsoluteDataDir(saved.DataDir),
            "GmOpenWhenEmpty" => saved.GmOpenWhenEmpty ?? !_explicitProduction && saved.LocalOnly(),
            "Panel.RequireAdmin2FA" => saved.Panel.RequireAdmin2FA ?? !saved.LocalOnly(),
            _ => setting.Get(saved),
        };
        return JsonSerializer.SerializeToElement(value, Json);
    }

    public async Task<PanelConfigResult> SaveAsync(string admin, PanelConfigUpdate update)
    {
        await _writer.WaitAsync();
        try { return await _loop.InvokeAsync(() => SaveOnLoopAsync(admin, update)); }
        finally { _writer.Release(); }
    }

    private async Task<PanelConfigResult> SaveOnLoopAsync(string admin, PanelConfigUpdate update)
    {
        if (!string.Equals(update.Revision, Revision(_file), StringComparison.Ordinal))
            return new(false, "Settings changed since this page was loaded. Reload before saving.", Snapshot());
        var overrides = ReadOverrides(_file);
        foreach (var (path, value) in update.Changes) overrides[path] = value;
        var desired = Clone(_initial);
        ApplyOverrides(desired, overrides);
        byte[]? previous = File.Exists(_file) ? await File.ReadAllBytesAsync(_file) : null;
        await PersistAsync(overrides);
        try
        {
            await _accounts.WriteAsync<bool>(new AdminAuditWritePlan(_clock.GetUtcNow().ToUnixTimeSeconds(), admin,
                "server.config", "server", "Saved settings: " + string.Join(", ", update.Changes.Keys.Order(StringComparer.Ordinal))));
        }
        catch
        {
            if (previous == null) File.Delete(_file);
            else await File.WriteAllBytesAsync(_file, previous);
            throw;
        }
        ApplyLive(desired, update.Changes.Keys);
        var snapshot = Snapshot();
        return new(true, snapshot.Settings.Any(s => s.PendingRestart)
            ? "Saved. Live settings are applied; settings marked Restart required take effect after a restart."
            : "Saved and applied. Future rewards use the new rates.", snapshot);
    }

    private async Task PersistAsync(Dictionary<string, JsonElement> overrides)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        string temporary = _file + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(overrides, Json));
                stream.Flush(flushToDisk: true);
            }
            PrivateFiles.Files(temporary);
            File.Move(temporary, _file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void ApplyLive(ServerOptions desired, IEnumerable<string> changes)
    {
        var changed = changes.ToHashSet(StringComparer.Ordinal);
        foreach (var setting in Schema(typeof(ServerOptions)).Where(s => s.Live && changed.Contains(s.Path)))
            setting.Set(_active, JsonSerializer.SerializeToElement(setting.Get(desired), Json));
        _characters.Experience = _active.Experience;
        if (changed.Contains("FieldExpRates")) _rewards.SetConfiguredFieldExpRules(desired.FieldExpRates);
    }

    private static IReadOnlyDictionary<string, string> LoadDescriptions()
    {
        using var stream = typeof(ServerConfiguration).Assembly.GetManifestResourceStream("Ro3Emu.Server.ConfigurationDescriptions.json");
        return stream == null ? new Dictionary<string, string>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new(StringComparer.Ordinal);
    }

    private string Help(Setting setting) => setting.Path switch
    {
        "FieldExpRates" => "Open-field EXP by player level. JSON rows: { MinLevel: 1, MaxLevel: 99, Percent: 200 }. Empty uses table rates. First matching row wins; stacks with monster rate and catch-up.",
        "Experience.MonsterPercent" => "All monster Base EXP: 100 = 1x, 200 = 2x. Does not scale quest or mail rewards. Stacks with field rates and catch-up.",
        "Experience.JobFromBasePercent" => "Job EXP generated from Base EXP, for all reward sources. Null uses the game table (normally 20%). Does not replace separately awarded Job EXP.",
        "Experience.CatchUpEnabled" => "Enable the table's extra EXP for characters below the world's level. Applies to monsters and direct EXP grants.",
        "Experience.LevelGapCorrectionEnabled" => "Adjust monster EXP for the level difference between player and monster.",
        "Experience.PartyBonusEnabled" => "Apply the existing party EXP bonus to monster kills.",
        "OperatorToken" => "Private operator token. Current value is never returned. Leave unchanged to keep it; a replacement takes effect after restart.",
        "DataDir" => "Persistent data directory. A change requires an offline data migration as well as a restart; it does not copy your database.",
        _ => _descriptions.GetValueOrDefault(setting.Chain[^1].DeclaringType!.Name + "." + setting.Chain[^1].Name)
            ?? "Ro3Emu:" + setting.Path.Replace('.', ':'),
    };

    public void Dispose() => _writer.Dispose();
}
