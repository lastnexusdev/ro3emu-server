using System.Text.Json;

namespace Ro3Emu.Panel;

public sealed record PanelConfigSetting(string Path, string Type, string Description, JsonElement Active,
    JsonElement Saved, bool Live, bool PendingRestart, bool Secret, bool Nullable);

public sealed record PanelServerConfiguration(string Revision, IReadOnlyList<PanelConfigSetting> Settings);

public sealed record PanelConfigUpdate(string Revision, Dictionary<string, JsonElement> Changes);

public sealed record PanelConfigResult(bool Done, string Message, PanelServerConfiguration Configuration);
