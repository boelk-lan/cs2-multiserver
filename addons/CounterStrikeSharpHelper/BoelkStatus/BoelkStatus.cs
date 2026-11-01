// boelk-lan's companion plugin for every CounterStrikeSharp-enabled server:
//
// - Pushes the current player list (and team scores) to backend-server on player connect, disconnect
//   and team switch, plus a heartbeat every 3 seconds so the backend can tell the server is alive.
//   Every message carries the full state, so a missed message is fixed by the next heartbeat.
// - Sends a "match_end" message with the final scoreboard when a match ends (cs_win_panel_match).
// - Runs MapStartCommands on every map start (per server type, e.g. endless warmup for vanilla).
// - EveryoneAdmin: gives every player @css/root, so everyone can use css_rcon / !rcon <command>.
//   Admins can also type common server-only commands (bot_add, mp_warmup_end, ...) directly in their
//   console: CS2 refuses those from players ("not marked FCVAR_CLIENT_CAN_EXECUTE"), so they're
//   intercepted and run on the server instead (ForwardedCommands).
// - PugSharpConfigFile: loaded once via ps_loadconfigfile after the server has started.
//
// Config (written by the CounterStrikeSharpHelper addon from CSS_BOELK_* settings in server.conf):
//   addons/counterstrikesharp/configs/plugins/BoelkStatus/BoelkStatus.json
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace BoelkStatus;

public class BoelkStatusConfig : BasePluginConfig
{
    [JsonPropertyName("Url")] public string Url { get; set; } = "";
    [JsonPropertyName("EveryoneAdmin")] public bool EveryoneAdmin { get; set; } = false;
    [JsonPropertyName("MapStartCommands")] public string MapStartCommands { get; set; } = "";
    [JsonPropertyName("PugSharpConfigFile")] public string PugSharpConfigFile { get; set; } = "";
}

public class BoelkStatusPlugin : BasePlugin, IPluginConfig<BoelkStatusConfig>
{
    public override string ModuleName => "BoelkStatus";
    public override string ModuleVersion => "0.6.0";
    public override string ModuleDescription => "Reports players, results and a heartbeat to the boelk-lan backend";

    public BoelkStatusConfig Config { get; set; } = new();
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    bool _pugSharpConfigLoaded;

    // Server-only commands an admin may type directly; anything else still works via css_rcon.
    static readonly string[] ForwardedCommands =
    [
        "bot_add", "bot_add_ct", "bot_add_t", "bot_kick", "bot_kill", "bot_quota", "bot_difficulty",
        "bot_stop", "bot_freeze", "bot_place", "bot_dont_shoot",
        "mp_warmup_end", "mp_warmup_start", "mp_warmup_pausetimer", "mp_restartgame", "mp_pause_match",
        "mp_unpause_match", "mp_maxrounds", "mp_roundtime", "mp_roundtime_defuse", "mp_freezetime",
        "mp_buytime", "mp_buy_anywhere", "mp_startmoney", "mp_maxmoney", "mp_autoteambalance",
        "mp_limitteams", "mp_friendlyfire", "mp_respawn_on_death_ct", "mp_respawn_on_death_t",
        "changelevel", "sv_infinite_ammo", "sv_grenade_trajectory_prac_pipreview", "sv_showimpacts",
        "sv_gravity", "host_timescale",
    ];

    public void OnConfigParsed(BoelkStatusConfig config) => Config = config;

    public override void Load(bool hotReload)
    {
        // An empty server hibernates by default: no ticks, so no timers - no heartbeat, no map start commands.
        Server.ExecuteCommand("sv_hibernate_when_empty 0");

        if (string.IsNullOrWhiteSpace(Config.Url))
            Logger.LogWarning("BoelkStatus: no Url configured, not reporting anything");
        else
        {
            Logger.LogInformation("BoelkStatus: reporting to {Url}", Config.Url);
            AddTimer(3.0f, () => Send("heartbeat"), TimerFlags.REPEAT);
        }

        foreach (var command in ForwardedCommands)
            AddCommandListener(command, ForwardToServer, HookMode.Pre);

        // Give the game's own map/gamemode configs time to run first, then override them.
        RegisterListener<Listeners.OnMapStart>(_ => AddTimer(3.0f, OnMapReady, TimerFlags.STOP_ON_MAPCHANGE));

        if (hotReload)
        {
            // Loaded mid-map (plugin update): leave the running game alone - no map start commands
            // (bot_quota 0 would kick all bots) and no PugSharp config (would reset the match).
            // Both apply again from the next map change.
            _pugSharpConfigLoaded = true;
            foreach (var p in HumanPlayers()) GrantAdmin(p);
        }
    }

    void OnMapReady()
    {
        if (!string.IsNullOrWhiteSpace(Config.MapStartCommands))
            Server.ExecuteCommand(Config.MapStartCommands);

        // Only once per server process: PugSharp changes the map itself after loading, and a later
        // map change must not restart the match.
        if (!_pugSharpConfigLoaded && !string.IsNullOrWhiteSpace(Config.PugSharpConfigFile))
        {
            _pugSharpConfigLoaded = true;
            Logger.LogInformation("BoelkStatus: loading PugSharp match config {File}", Config.PugSharpConfigFile);
            Server.ExecuteCommand($"ps_loadconfigfile {Config.PugSharpConfigFile}");
        }
    }

    void GrantAdmin(CCSPlayerController player)
    {
        if (Config.EveryoneAdmin && !player.IsBot)
            AdminManager.AddPlayerPermissions(player, "@css/root");
    }

    HookResult ForwardToServer(CCSPlayerController? player, CommandInfo info)
    {
        // Server console / non-admins: leave the command to CS2 as usual
        if (player == null || !AdminManager.PlayerHasPermissions(player, "@css/root")) return HookResult.Continue;
        var command = info.GetCommandString;
        Logger.LogInformation("BoelkStatus: {Player} ran {Command}", player.PlayerName, command);
        RunServerCommand(command);
        return HookResult.Handled;
    }

    // CS2's bot_add and its bot quota manager fight each other: every bot_add ends up adding extra bots
    // (often on the other team), in every bot_quota_mode. So bot_add* is done through the quota manager
    // alone: bot_quota_mode normal, bot_quota = number of bots, bot_join_team picks the side.
    void RunServerCommand(string command)
    {
        var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var name = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var quota = ConVar<int>.Find("bot_quota")?.Value ?? 0;

        string? team = name switch { "bot_add" => "any", "bot_add_ct" => "ct", "bot_add_t" => "t", _ => null };
        if (team != null)
        {
            Server.ExecuteCommand($"bot_quota_mode normal; bot_join_team {team}; bot_quota {quota + 1}");
            AddTimer(2.0f, () => Server.ExecuteCommand("bot_join_team any"));
            return;
        }

        Server.ExecuteCommand(command);
        // Otherwise the quota manager immediately adds the kicked bots back
        if (name == "bot_kick")
            AddTimer(0.5f, () =>
                Server.ExecuteCommand($"bot_quota {Utilities.GetPlayers().Count(p => p is { IsValid: true, IsBot: true, IsHLTV: false })}"));
    }

    [ConsoleCommand("css_rcon", "Runs a server command: !rcon <command>")]
    [RequiresPermissions("@css/root")]
    public void OnRcon(CCSPlayerController? player, CommandInfo info)
    {
        var command = info.ArgString.Trim();
        if (command.Length == 0)
        {
            info.ReplyToCommand("Usage: !rcon <command>, e.g. !rcon mp_warmup_end");
            return;
        }
        Logger.LogInformation("BoelkStatus: {Player} ran rcon: {Command}", player?.PlayerName ?? "console", command);
        RunServerCommand(command);
        info.ReplyToCommand($"Executed: {command}");
    }

    [GameEventHandler]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull e, GameEventInfo info)
    {
        if (e.Userid is { IsValid: true } player) GrantAdmin(player);
        Send("connect");
        return HookResult.Continue;
    }

    // Fires before the player is gone, so leave that player out explicitly.
    [GameEventHandler]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect e, GameEventInfo info)
    {
        Send("disconnect", excludeSlot: e.Userid?.Slot);
        return HookResult.Continue;
    }

    // Fires before the team actually changes, so read the players one frame later.
    [GameEventHandler]
    public HookResult OnPlayerTeam(EventPlayerTeam e, GameEventInfo info)
    {
        Server.NextFrame(() => Send("team"));
        return HookResult.Continue;
    }

    // End-of-match scoreboard (shown on the win panel), final scores and stats.
    [GameEventHandler]
    public HookResult OnMatchEnd(EventCsWinPanelMatch e, GameEventInfo info)
    {
        Send("match_end");
        return HookResult.Continue;
    }

    static IEnumerable<CCSPlayerController> HumanPlayers() =>
        Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false });

    // Must run on the main thread (game state isn't thread-safe); only the HTTP call goes off-thread.
    void Send(string evt, int? excludeSlot = null)
    {
        if (string.IsNullOrWhiteSpace(Config.Url)) return;

        var players = Utilities.GetPlayers()
            .Where(p => p is { IsValid: true, IsHLTV: false } && p.Slot != excludeSlot)
            .Select(p =>
            {
                // The game's own scoreboard stats for the whole match (reset by mp_restartgame)
                var m = p.ActionTrackingServices?.MatchStats;
                return new
                {
                    steamId = p.SteamID.ToString(),
                    name = p.PlayerName,
                    team = p.Team.ToString(),
                    isBot = p.IsBot,
                    ping = p.Ping,
                    score = p.Score,
                    mvps = p.MVPs,
                    kills = m?.Kills ?? 0,
                    deaths = m?.Deaths ?? 0,
                    assists = m?.Assists ?? 0,
                    damage = m?.Damage ?? 0,
                    headshotKills = m?.HeadShotKills ?? 0,
                    utilityDamage = m?.UtilityDamage ?? 0,
                    enemiesFlashed = m?.EnemiesFlashed ?? 0,
                    enemy2Ks = m?.Enemy2Ks ?? 0,
                    enemy3Ks = m?.Enemy3Ks ?? 0,
                    enemy4Ks = m?.Enemy4Ks ?? 0,
                    enemy5Ks = m?.Enemy5Ks ?? 0,
                    knifeKills = m?.EnemyKnifeKills ?? 0,
                    taserKills = m?.EnemyTaserKills ?? 0,
                    entryCount = m?.EntryCount ?? 0,
                    entryWins = m?.EntryWins ?? 0,
                    clutch1v1Count = m?.I1v1Count ?? 0,
                    clutch1v1Wins = m?.I1v1Wins ?? 0,
                    clutch1v2Count = m?.I1v2Count ?? 0,
                    clutch1v2Wins = m?.I1v2Wins ?? 0,
                    cashEarned = m?.CashEarned ?? 0,
                    moneySaved = m?.MoneySaved ?? 0,
                    objective = m?.Objective ?? 0,
                };
            })
            .ToList();
        // cs_team_manager entities: TeamNum 2 = Terrorist, 3 = CounterTerrorist
        var teams = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager")
            .Where(t => t.TeamNum is 2 or 3)
            .Select(t => new { team = t.TeamNum == 2 ? "Terrorist" : "CounterTerrorist", name = t.ClanTeamname, score = t.Score })
            .ToList();
        // GamePhase 5 = GAMEPHASE_MATCH_ENDED
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        var phase = rules == null ? "unknown" : rules.WarmupPeriod ? "warmup" : rules.GamePhase == 5 ? "ended" : "live";
        var body = new { @event = evt, map = Server.MapName, phase, roundsPlayed = rules?.TotalRoundsPlayed ?? 0, players, teams };
        var url = Config.Url;

        Task.Run(async () =>
        {
            // ponytail: errors are swallowed, the next heartbeat retries anyway (match_end is not retried)
            try { await Http.PostAsJsonAsync(url, body); } catch { }
        });
    }
}
