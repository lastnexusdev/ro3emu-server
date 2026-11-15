namespace Ro3Emu.Server;

/// <summary>Operator overrides, not original-server evidence. Applied to future rewards, never existing EXP.</summary>
public sealed class ExperienceOptions
{
    /// <summary>All monster Base EXP, after level, party and field adjustments; 100 = unchanged, 200 = double.</summary>
    public int MonsterPercent { get; set; } = 100;

    /// <summary>Job EXP generated from Base EXP, in percent. Null uses PublicParamConfig.BaseToJobConversionPercent.</summary>
    public int? JobFromBasePercent { get; set; }

    /// <summary>Apply BaseExpAddConfig's catch-up bonus to monster kills and direct EXP grants.</summary>
    public bool CatchUpEnabled { get; set; } = true;

    /// <summary>Apply LvExpCorrectionConfig to monster rewards.</summary>
    public bool LevelGapCorrectionEnabled { get; set; } = true;

    /// <summary>Apply the existing party EXP bonus to monster rewards.</summary>
    public bool PartyBonusEnabled { get; set; } = true;

    internal static long Scale(long amount, int percent) => checked((long)((decimal)amount * percent / 100));
}
