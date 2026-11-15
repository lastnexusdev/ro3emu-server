# Server configs

Admins can open **Admin > Server configs** to search and edit the server's `ServerOptions`, including nested panel,
anti-cheat, backup, voice and gameplay options. Booleans use selectors, numbers use numeric inputs, field EXP ranges
have a row editor, and other collections use JSON. The active value appears under each saved value. Private operator
tokens and Discord webhook URLs are write-only: leaving the replacement field empty keeps the existing value.

**Save and apply** validates the whole batch before writing anything. Saves use a revision to refuse stale edits,
replace the configuration file atomically, and record the changed setting names in the admin audit log without
recording secrets. A failed save does not apply live changes. Saving never restarts the server.

Overrides live in `server-config.json` in the original configured data directory. They take precedence over the base
configuration on subsequent starts. The file contains only edited settings; settings not overridden continue to use
the ordinary configuration. Keep this file private alongside the account database and include it in external backups;
the panel's database backups do not include it. A `DataDir` edit changes the directory used at the next start but does
not migrate the database: move data offline first. Overrides continue to be read from the original configured data
directory, which allows that change to survive a restart.

## Live changes

These operator policies are inferred, not claims about the original game server:

- `Experience.MonsterPercent`: all monster Base EXP, 100 for unchanged, 200 for double. Does not multiply quest or
  mail Base EXP. A zero rate disables monster Base EXP and the Job EXP generated from it.
- `FieldExpRates`: additional open-field multipliers by player level. First matching range wins. These stack with
  the monster multiplier. An empty list removes the custom field multiplier. Saving also updates the rates that
  `!fieldexp reset` restores; a GM's unsaved `!fieldexp set` remains temporary and is shown as the active value.
- `Experience.JobFromBasePercent`: Job EXP generated from credited Base EXP, for all sources. Null keeps the
  exported `PublicParamConfig.BaseToJobConversionPercent` (normally 20%). Separate Job EXP awards are added to it.
  The historical one-time Job EXP repair keeps its table-based ratio so changing this setting does not create
  retroactive compensation.
- `Experience.CatchUpEnabled`: the existing below-world-level bonus for kills and direct EXP grants.
- `Experience.LevelGapCorrectionEnabled`: the existing monster/player level difference correction.
- `Experience.PartyBonusEnabled`: the existing party EXP bonus.
- `StarCoreDrops`, `TitleCrestPercent`, `TavernDayDrinks`, `RaidPreparationSeconds`: apply to subsequent relevant
  actions; existing drops, earned drinks and already scheduled encounter timers are not recalculated.

Live changes run on the game loop after persistence and audit. EXP changes affect future reward calculations and do
not rewrite stored character EXP. Defaults preserve the previous reward calculations.

Other settings are marked **Restart required**. Their new values are saved while their active values remain unchanged.
**Restart pending** means a saved value differs from the running value. Use Server control when ready to restart;
under a manual launcher the process may need to be started again.

For Windows installations, the [Windows supervisor](windows-supervisor.md) relaunches a panel restart while respecting
an intentional shutdown and starting automatically after Windows boots.

This editor manages server options, not the thousands of exported client game-data tables. Those tables have cached
typed views and client-side counterparts and are not advertised as safe to reload live.
