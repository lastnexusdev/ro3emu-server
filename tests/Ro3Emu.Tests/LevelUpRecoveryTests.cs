using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>All EXP reward paths refill a living character only when its base level rises.</summary>
public sealed class LevelUpRecoveryTests
{
    [Theory]
    [InlineData("direct", true, false)]
    [InlineData("award", true, false)]
    [InlineData("exchange", true, false)]
    [InlineData("direct", false, false)]
    [InlineData("award", false, false)]
    [InlineData("exchange", false, false)]
    [InlineData("direct", true, true)]
    [InlineData("award", true, true)]
    [InlineData("exchange", true, true)]
    public async Task ExpRefillsHpSpOnlyOnLivingBaseLevelUp(string path, bool levelUp, bool dead)
    {
        await using var host = await SyntheticHost.StartAsync();
        var character = host.Service<CharacterService>();
        character.ExtraAttributes.Add(_ => [(101, 100), (102, 50)]);
        await using var bot = await host.InWorldAsync("Recovery");
        var loop = host.Service<GameLoop>();
        var session = host.Service<SessionRegistry>().All().Single(s => s.Role?.Name == "Recovery");
        await loop.InvokeAsync(async () =>
        {
            session.NoRegenUntilMs = long.MaxValue;
            session.Dead = dead;
            await character.SetHpSpAsync(session, dead ? 0 : 1, 0);
            int oldLevel = session.Progress!.Level;
            int exp = levelUp ? 6 : 1; // Invented fixture: level 1 requires 6 EXP.
            switch (path)
            {
                case "direct": await character.AddExpAsync(session, exp, 0); break;
                case "award":
                    Assert.True(await character.ApplyAwardAsync(session, new AwardPlan(0, 0, []), taskBaseExp: exp));
                    break;
                case "exchange":
                    using (await CharacterService.Lock(session))
                        Assert.NotNull(await character.ExchangeLockedAsync(session, [], [], popup: false, baseExp: exp));
                    break;
            }
            Assert.Equal(levelUp, session.Progress.Level > oldLevel);
            var stats = character.Stats(session);
            Assert.Equal(levelUp && !dead ? stats[101] : dead ? 0 : 1, stats[2000]);
            Assert.Equal(levelUp && !dead ? stats[102] : 0, stats[2001]);
            Assert.Equal(dead, session.Dead);
        });
    }
}
