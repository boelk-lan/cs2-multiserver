// Reports arena kills on K4-Arenas servers to boelk-lan's backend-server, which computes the ELO,
// and appends each player's ELO or rank to K4-Arenas' scoreboard clan tag ("ARENA 1 | 1016" or
// "ARENA 1 | GN3" - the backend decides which, set in the frontend's ELO tab).
//
// Every kill by an enemy player outside warmup is rated as a duel between killer (wins) and victim
// (loses), in 1v1 rounds and team rounds (2v2, 3v3, ...) alike. Suicides, fall damage and draws don't
// count. Bots are rated too; the backend tells them apart by name (they all have SteamID 0).
// Uses only game events, not K4-Arenas' shared API: looking that API up failed on some servers
// (KeyNotFoundException 'k4-arenas:sharedapi') even though K4-Arenas itself ran fine.
//
// Tags and settings come from the backend: GET <backend>/api/elo on load and every round start (so
// edits, resets and settings from the frontend show up in-game), and both players' new tags in the
// response to each duel report.
//
// Filler bot (off unless enabled in the frontend): with an uneven number of human players, adds one bot
// (bot_quota 1) so everyone gets an opponent, removes it again when the count is even. Bots only move on
// arena maps with a navigation mesh.
//
// Config (written by the CounterStrikeSharpHelper addon from CSS_BOELK_ARENA_URL):
//   addons/counterstrikesharp/configs/plugins/BoelkArena/BoelkArena.json -> { "Url": "http://..." }
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace BoelkArena;

public class BoelkArenaConfig : BasePluginConfig
{
    [JsonPropertyName("Url")] public string Url { get; set; } = "";
}

public class BoelkArenaPlugin : BasePlugin, IPluginConfig<BoelkArenaConfig>
{
    public override string ModuleName => "BoelkArena";
    public override string ModuleVersion => "0.4.0";
    public override string ModuleDescription => "Reports K4-Arenas kills to the boelk-lan backend (ELO) and shows the ELO or rank in the clan tag";

    public BoelkArenaConfig Config { get; set; } = new();
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    // Player key (SteamID64, bots "bot:<name>", see backend elo.ts playerKey) -> clan tag text. Replaced
    // as a whole on every ranking load (so a reset in the frontend clears it); written by HTTP callbacks,
    // read on the main thread.
    volatile ConcurrentDictionary<string, string> _tags = new();
    volatile string _defaultTag = "1000"; // players without a rating yet
    volatile bool _fillBot;

    static string Key(CCSPlayerController p) => p.IsBot ? $"bot:{p.PlayerName}" : p.SteamID.ToString();

    public void OnConfigParsed(BoelkArenaConfig config) => Config = config;

    // The duel webhook is .../api/instances/<name>/duel-events; the ranking lives at .../api/elo
    string? EloUrl => Config.Url.IndexOf("/api/", StringComparison.Ordinal) is var i and >= 0
        ? Config.Url[..i] + "/api/elo"
        : null;

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnMapStart>(_ => { _botQuota = -1; LoadRatings(); }); // map start resets bot_quota (CSS_BOELK_MAP_COMMANDS)
        LoadRatings();
        Logger.LogInformation("BoelkArena: reporting to {Url}", Config.Url);
    }

    void LoadRatings()
    {
        var url = EloUrl;
        if (url == null) return;
        Task.Run(async () =>
        {
            try
            {
                var response = await Http.GetFromJsonAsync<EloResponse>(url);
                if (response == null) return;
                _tags = new(response.Ranking.ToDictionary(p => p.SteamId, p => p.Tag));
                _defaultTag = response.DefaultTag;
                _fillBot = response.Settings.FillBot;
                Server.NextFrame(() => { UpdateAllTags(); UpdateBotQuota(); });
            }
            catch { } // backend down: keep the last tags and settings
        });
    }

    // K4-Arenas sets its arena tag ("ARENA 1 |") at round start; append the ELO right after that
    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart e, GameEventInfo info)
    {
        LoadRatings();
        AddTimer(0.5f, UpdateAllTags, TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    // K4-Arenas pairs players at round start, so fix the bot count before that: at round end and when
    // players join or leave (a leaving player is still listed during the event, so check a moment later)
    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd e, GameEventInfo info) { UpdateBotQuota(); return HookResult.Continue; }

    [GameEventHandler]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull e, GameEventInfo info) { UpdateBotQuota(); return HookResult.Continue; }

    [GameEventHandler]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect e, GameEventInfo info)
    {
        AddTimer(0.1f, UpdateBotQuota, TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    int _botQuota = -1;

    void UpdateBotQuota()
    {
        var humans = Utilities.GetPlayers().Count(p => p is { IsValid: true, IsBot: false, IsHLTV: false });
        var quota = _fillBot ? humans % 2 : 0;
        if (quota == _botQuota) return;
        _botQuota = quota;
        Server.ExecuteCommand($"bot_quota_mode normal; bot_quota {quota}");
    }

    // K4's part of the tag: everything up to and including its "|", or "" if it set none
    static string ArenaPrefix(string? clan) => clan?.IndexOf('|') is int i and >= 0 ? clan[..(i + 1)] : "";

    void UpdateAllTags()
    {
        foreach (var player in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsHLTV: false }))
        {
            var prefix = ArenaPrefix(player.Clan);
            var tag = $"{prefix}{(prefix == "" ? "" : " ")}{_tags.GetValueOrDefault(Key(player), _defaultTag)}";
            if (player.Clan == tag) continue;
            player.Clan = tag;
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_szClan");
        }
    }

    // Prints every player's current clan tag to the server console (for checking the tags)
    [ConsoleCommand("css_arena_tags", "Lists the arena/ELO clan tags of all players")]
    public void OnArenaTags(CCSPlayerController? caller, CommandInfo info)
    {
        foreach (var p in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsHLTV: false }))
            info.ReplyToCommand($"[{p.Clan}] {p.PlayerName}");
    }

    static bool IsWarmup() =>
        Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules?.WarmupPeriod ?? false;

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath e, GameEventInfo info)
    {
        var loser = e.Userid;
        var winner = e.Attacker;
        if (string.IsNullOrWhiteSpace(Config.Url) || loser is not { IsValid: true } || winner is not { IsValid: true }
            || winner == loser || winner.Team == loser.Team || IsWarmup())
            return HookResult.Continue;

        var body = new
        {
            @event = "duel",
            arena = ArenaPrefix(loser.Clan).TrimEnd('|').Trim(),
            winner = new { steamId = winner.SteamID.ToString(), name = winner.PlayerName, isBot = winner.IsBot },
            loser = new { steamId = loser.SteamID.ToString(), name = loser.PlayerName, isBot = loser.IsBot },
        };
        var url = Config.Url;
        Task.Run(async () =>
        {
            // ponytail: a failed send loses this duel's rating change; no retry queue
            try
            {
                var response = await Http.PostAsJsonAsync(url, body);
                var result = await response.Content.ReadFromJsonAsync<DuelResponse>();
                foreach (var (key, tag) in result?.Tags ?? []) _tags[key] = tag;
                Server.NextFrame(UpdateAllTags);
            }
            catch { }
        });
        return HookResult.Continue;
    }

    record RankingEntry([property: JsonPropertyName("steamId")] string SteamId, [property: JsonPropertyName("tag")] string Tag);
    record Settings([property: JsonPropertyName("fillBot")] bool FillBot);
    record EloResponse(
        [property: JsonPropertyName("ranking")] List<RankingEntry> Ranking,
        [property: JsonPropertyName("settings")] Settings Settings,
        [property: JsonPropertyName("defaultTag")] string DefaultTag);
    record DuelResponse([property: JsonPropertyName("tags")] Dictionary<string, string> Tags);
}
