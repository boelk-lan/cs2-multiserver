// Reports 1v1 arena duels on K4-Arenas servers to boelk-lan's backend-server, which computes the ELO,
// and shows each player's arena and ELO as their scoreboard clan tag ("ARENA 1 | 1016").
//
// A duel is a death in an arena where the victim had exactly one opponent (K4-Arenas' FindOpponents):
// that opponent wins. Team arenas (2v2, 3v3, ...) don't count. Deaths without an enemy killer
// (fall damage, suicide) also count as a win for the opponent; draws (time runs out) don't count.
// Bots are reported too (steamId "0"); the backend leaves them out of the rating.
//
// Ratings come from the backend: the whole ranking (GET <backend>/api/elo) on load and map start,
// and both players' new ratings in the response to each duel report.
//
// Config (written by the CounterStrikeSharpHelper addon from CSS_BOELK_ARENA_URL):
//   addons/counterstrikesharp/configs/plugins/BoelkArena/BoelkArena.json -> { "Url": "http://..." }
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using K4ArenaSharedApi;
using Microsoft.Extensions.Logging;

namespace BoelkArena;

public class BoelkArenaConfig : BasePluginConfig
{
    [JsonPropertyName("Url")] public string Url { get; set; } = "";
}

public class BoelkArenaPlugin : BasePlugin, IPluginConfig<BoelkArenaConfig>
{
    public override string ModuleName => "BoelkArena";
    public override string ModuleVersion => "0.2.0";
    public override string ModuleDescription => "Reports K4-Arenas 1v1 duels to the boelk-lan backend (ELO) and shows arena + ELO as clan tag";

    const int StartRating = 1000; // backend-server/src/elo.ts START_RATING

    public BoelkArenaConfig Config { get; set; } = new();
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    static readonly PluginCapability<IK4ArenaSharedApi> ArenaApi = new("k4-arenas:sharedapi");
    // SteamID64 -> rating; written by HTTP callbacks, read on the main thread
    readonly ConcurrentDictionary<string, int> _ratings = new();

    public void OnConfigParsed(BoelkArenaConfig config) => Config = config;

    // The duel webhook is .../api/instances/<name>/duel-events; the ranking lives at .../api/elo
    string? EloUrl => Config.Url.IndexOf("/api/", StringComparison.Ordinal) is var i and >= 0
        ? Config.Url[..i] + "/api/elo"
        : null;

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnMapStart>(_ => LoadRatings());
        LoadRatings();
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        Logger.LogInformation("BoelkArena: K4-Arenas API {State}, reporting to {Url}",
            ArenaApi.Get() == null ? "NOT FOUND" : "found", Config.Url);
    }

    void LoadRatings()
    {
        var url = EloUrl;
        if (url == null) return;
        Task.Run(async () =>
        {
            try
            {
                var response = await Http.GetFromJsonAsync<RankingResponse>(url);
                foreach (var p in response?.Ranking ?? []) _ratings[p.SteamId] = p.Rating;
                Server.NextFrame(UpdateAllTags);
            }
            catch { } // backend down: tags show the start rating until the next map / duel
        });
    }

    // K4-Arenas puts players into their arenas at round start; set the tags right after that
    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart e, GameEventInfo info)
    {
        AddTimer(0.5f, UpdateAllTags, TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    void UpdateAllTags()
    {
        var api = ArenaApi.Get();
        if (api == null) return;
        foreach (var player in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsHLTV: false }))
        {
            var arena = api.GetArenaName(player);
            var tag = player.IsBot
                ? arena
                : $"{arena}{(string.IsNullOrEmpty(arena) ? "" : " | ")}{_ratings.GetValueOrDefault(player.SteamID.ToString(), StartRating)}";
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

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath e, GameEventInfo info)
    {
        var api = ArenaApi.Get();
        var victim = e.Userid;
        if (api == null || string.IsNullOrWhiteSpace(Config.Url) || victim is not { IsValid: true }) return HookResult.Continue;

        var opponents = api.FindOpponents(victim);
        if (opponents is not { Count: 1 }) return HookResult.Continue; // team arena or no arena
        var winner = opponents[0];
        var killer = e.Attacker;
        // Killed by someone else than the opponent (shouldn't happen in an arena): not a clean duel
        if (killer is { IsValid: true } && killer != victim && killer != winner) return HookResult.Continue;
        if (winner is not { IsValid: true }) return HookResult.Continue;

        var body = new
        {
            @event = "duel",
            arena = api.GetArenaName(victim),
            winner = new { steamId = winner.SteamID.ToString(), name = winner.PlayerName, isBot = winner.IsBot },
            loser = new { steamId = victim.SteamID.ToString(), name = victim.PlayerName, isBot = victim.IsBot },
        };
        var url = Config.Url;
        Task.Run(async () =>
        {
            // ponytail: a failed send loses this duel's rating change; no retry queue
            try
            {
                var response = await Http.PostAsJsonAsync(url, body);
                var result = await response.Content.ReadFromJsonAsync<DuelResponse>();
                foreach (var (steamId, rating) in result?.Ratings ?? []) _ratings[steamId] = rating;
                Server.NextFrame(UpdateAllTags);
            }
            catch { }
        });
        return HookResult.Continue;
    }

    record RankingEntry([property: JsonPropertyName("steamId")] string SteamId, [property: JsonPropertyName("rating")] int Rating);
    record RankingResponse([property: JsonPropertyName("ranking")] List<RankingEntry> Ranking);
    record DuelResponse([property: JsonPropertyName("ratings")] Dictionary<string, int> Ratings);
}
