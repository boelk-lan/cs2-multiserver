// Reports 1v1 arena duels on K4-Arenas servers to boelk-lan's backend-server, which computes the ELO.
//
// A duel is a death in an arena where the victim had exactly one opponent (K4-Arenas' FindOpponents):
// that opponent wins. Team arenas (2v2, 3v3, ...) don't count. Deaths without an enemy killer
// (fall damage, suicide) also count as a win for the opponent; draws (time runs out) don't count.
// Bots are reported too (steamId "0"); the backend leaves them out of the rating.
//
// Config (written by the CounterStrikeSharpHelper addon from CSS_BOELK_ARENA_URL):
//   addons/counterstrikesharp/configs/plugins/BoelkArena/BoelkArena.json -> { "Url": "http://..." }
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
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
    public override string ModuleVersion => "0.1.0";
    public override string ModuleDescription => "Reports K4-Arenas 1v1 duels to the boelk-lan backend (ELO)";

    public BoelkArenaConfig Config { get; set; } = new();
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    static readonly PluginCapability<IK4ArenaSharedApi> ArenaApi = new("k4-arenas:sharedapi");

    public void OnConfigParsed(BoelkArenaConfig config) => Config = config;

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        Logger.LogInformation("BoelkArena: K4-Arenas API {State}, reporting to {Url}",
            ArenaApi.Get() == null ? "NOT FOUND" : "found", Config.Url);
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
            try { await Http.PostAsJsonAsync(url, body); } catch { }
        });
        return HookResult.Continue;
    }
}
