// Continuous 1v1 arena duels with ELO, for arena maps (am_*). Replaces K4-Arenas.
//
// The CS2 round never ends (mp_ignore_round_win_conditions); duels start whenever players are free:
//   - A duel ends when one of the two dies (any cause): the other one wins. The winner gets full health
//     and armor and waits alive in their arena; the loser waits dead.
//   - As soon as 4 or more players are waiting, they are shuffled into new pairs (avoiding rematches
//     where possible); with an odd count one keeps waiting.
//   - With 2-3 waiting, they are paired right away, except two who just fought each other: they only get a
//     rematch once no other duel is running. With 3 waiting, the last loser sits out, so the winner plays
//     the one who waited ("winner stays").
//   - Each duel picks a random round type both players have enabled (!rounds), and one random weapon
//     from that round type's list - the same weapon for both.
// Round types (name, weapons, armor) are in this plugin's config file, see BoelkArenaConfig.
//
// ELO: every finished duel is POSTed to the backend (winner, loser), which computes the ratings.
// Tags: the scoreboard clan tag shows "ARENA 3 | <tag>" or "WAIT | <tag>", where <tag> (ELO number or
// rank short name) comes from the backend. The backend pushes its full ELO state over <backend>/ws/events
// after every change; the plugin stays connected (and fetches GET /api/elo once per (re)connect).
// Bots are rated too; the backend tells them apart by name (they all have SteamID 0).
//
// Filler bot (off unless enabled in the frontend): with an uneven number of human players, adds one bot
// (bot_quota 1) so everyone gets an opponent. Bots only move on arena maps with a navigation mesh.
//
// Config: addons/counterstrikesharp/configs/plugins/BoelkArena/BoelkArena.json; the CounterStrikeSharpHelper
// addon sets Url from CSS_BOELK_ARENA_URL on every start and keeps everything else.
using System.Collections.Concurrent;
using System.Drawing;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace BoelkArena;

public class RoundType
{
    public string Name { get; set; } = "";
    public List<string> Weapons { get; set; } = []; // one is picked per duel, same for both; [] = knife only
    public List<string> Secondary { get; set; } = []; // optional, also one per duel for both
    public bool Armor { get; set; } = true;
    public bool Helmet { get; set; } = true;
    public bool EnabledByDefault { get; set; } = true;
    public bool Knife { get; set; } = true; // also give a knife
    public bool Invisible { get; set; } = false; // player models invisible (the "Shadow" round)
    public bool Custom { get; set; } = false; // each player gets their own weapon, picked with !weapon
    // Give the weapon again RefillSeconds after it's used up (grenades thrown, taser fired)
    public bool Refill { get; set; } = false;
    public float RefillSeconds { get; set; } = 2f;
}

public class BoelkArenaConfig : BasePluginConfig
{
    [JsonPropertyName("Url")] public string Url { get; set; } = "";

    // Run on every map start: never-ending round, no warmup, no buying or default weapons, no respawning
    [JsonPropertyName("ServerCommands")]
    public string ServerCommands { get; set; } =
        "mp_warmuptime 0; mp_warmup_end; mp_ignore_round_win_conditions 1; mp_roundtime 60; mp_roundtime_defuse 60; " +
        "mp_roundtime_hostage 60; mp_timelimit 0; mp_maxrounds 0; mp_halftime 0; mp_freezetime 0; " +
        "mp_respawn_on_death_t 0; mp_respawn_on_death_ct 0; mp_respawn_immunitytime 0; mp_join_grace_time 0; " +
        "mp_death_drop_gun 0; mp_death_drop_grenade 0; mp_buytime 0; mp_maxmoney 0; mp_playercashawards 0; " +
        "mp_teamcashawards 0; mp_free_armor 0; mp_autoteambalance 0; mp_limitteams 0; " +
        "mp_ct_default_primary \"\"; mp_t_default_primary \"\"; mp_ct_default_secondary \"\"; mp_t_default_secondary \"\"; " +
        "sv_disable_radar 1; mp_forcecamera 0; sv_deadtalk 1; sv_talk_enemy_dead 1; sv_talk_enemy_living 1";

    [JsonPropertyName("RoundTypes")]
    public List<RoundType> RoundTypes { get; set; } =
    [
        new() { Name = "Rifle", Weapons = ["weapon_ak47", "weapon_m4a1_silencer", "weapon_m4a1", "weapon_galilar", "weapon_famas", "weapon_sg556", "weapon_aug"] },
        new() { Name = "Sniper", Weapons = ["weapon_awp", "weapon_ssg08", "weapon_scar20", "weapon_g3sg1"] },
        new() { Name = "Shotgun", Weapons = ["weapon_xm1014", "weapon_nova", "weapon_mag7", "weapon_sawedoff"] },
        new() { Name = "SMG", Weapons = ["weapon_mac10", "weapon_mp9", "weapon_mp7", "weapon_p90", "weapon_mp5sd", "weapon_bizon", "weapon_ump45"] },
        new() { Name = "LMG", Weapons = ["weapon_m249", "weapon_negev"] },
        new() { Name = "Pistol", Weapons = ["weapon_deagle", "weapon_glock", "weapon_usp_silencer", "weapon_hkp2000", "weapon_elite", "weapon_tec9", "weapon_p250", "weapon_cz75a", "weapon_fiveseven", "weapon_revolver"] },
        new() { Name = "Scout", Weapons = ["weapon_ssg08"] },
        new() { Name = "AWP", Weapons = ["weapon_awp"] },
        new() { Name = "Deagle", Weapons = ["weapon_deagle"], Armor = false, Helmet = false },
        new() { Name = "Knife", Weapons = [], Armor = false, Helmet = false },
        new() { Name = "Frag nade", Weapons = ["weapon_hegrenade"], Armor = false, Helmet = false, Knife = false, Refill = true },
        new() { Name = "Molotov", Weapons = ["weapon_molotov"], Armor = false, Helmet = false, Knife = false, Refill = true, RefillSeconds = 3f },
        new() { Name = "Custom", Custom = true },
        new() { Name = "Shadow", Weapons = [], Armor = false, Helmet = false, Invisible = true },
        new() { Name = "Taser", Weapons = ["weapon_taser"], Armor = false, Helmet = false, Knife = false, Refill = true, RefillSeconds = 1.5f },
    ];
}

public class BoelkArenaPlugin : BasePlugin, IPluginConfig<BoelkArenaConfig>
{
    public override string ModuleName => "BoelkArena";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleDescription => "Continuous 1v1 arena duels with ELO from the boelk-lan backend";

    public BoelkArenaConfig Config { get; set; } = new();
    public void OnConfigParsed(BoelkArenaConfig config) => Config = config;

    // ---------------------------------------------------------------- duel state (main thread only)

    class Duel(Arena arena, CCSPlayerController t, CCSPlayerController ct, RoundType type, string? weapon, string? secondary)
    {
        public Arena Arena = arena;
        public float StartedAt = Server.CurrentTime;
        public bool RespawnRetried;
        public CCSPlayerController T = t, Ct = ct;
        public RoundType Type = type;
        public string? Weapon = weapon, Secondary = secondary;
        public bool Has(CCSPlayerController p) => p == T || p == Ct;
        public CCSPlayerController Other(CCSPlayerController p) => p == T ? Ct : T;
    }

    // Waiting player; Arena = the arena a waiting winner is still standing in (kept free of other duels)
    class Waiter(CCSPlayerController player, Arena? arena)
    {
        public CCSPlayerController Player = player;
        public Arena? Arena = arena;
    }

    List<Arena> _arenas = [];
    readonly List<Duel> _duels = [];
    readonly List<Waiter> _waiting = [];
    readonly Dictionary<int, int> _lastOpponent = new(); // player slot -> slot of their last opponent
    readonly HashSet<int> _pendingSetup = []; // respawned for a duel, set up on spawn
    CCSPlayerController? _lastLoser;

    Duel? DuelOf(CCSPlayerController p) => _duels.FirstOrDefault(d => d.Has(p));
    static bool IsPlaying(CCSPlayerController p) => p is { IsValid: true, IsHLTV: false } && p.Team > CsTeam.Spectator;
    // LifeState, not Health: a player who joined a team but never spawned has a pawn with 100 health while dead
    static bool IsAlive(CCSPlayerController p) =>
        p.PlayerPawn.Value is { } pawn && pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE && pawn.Health > 0;

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _botQuota = -1; // map start resets bot_quota (CSS_BOELK_MAP_COMMANDS)
            _arenas = [];
            _duels.Clear();
            _waiting.Clear();
            AddTimer(1f, SetupMap, TimerFlags.STOP_ON_MAPCHANGE);
        });
        LoadRoundPrefs();
        // Matching runs every second (not as a one-shot timer after each change: those die on map
        // changes and left matching stuck). Waiting players get matched within a second.
        AddTimer(1f, Match, TimerFlags.REPEAT);
        if (Backend is { } backend) Task.Run(() => ListenForUpdates(backend));
        if (hotReload) SetupMap();
        Logger.LogInformation("BoelkArena: reporting to {Url}", Config.Url);
    }

    public override void Unload(bool hotReload) => _stop.Cancel();

    void SetupMap()
    {
        Server.ExecuteCommand(Config.ServerCommands);
        _arenas = ArenaFinder.Find();
        Logger.LogInformation("BoelkArena: {Count} arena(s) on {Map}", _arenas.Count, Server.MapName);
        Restart();
    }

    // Everyone back to waiting (map start, new CS2 round); players still alive are slain
    void Restart()
    {
        _duels.Clear();
        _waiting.Clear();
        _pendingSetup.Clear();
        foreach (var p in Utilities.GetPlayers().Where(IsPlaying))
        {
            _waiting.Add(new Waiter(p, null));
            if (IsAlive(p)) p.PlayerPawn.Value!.CommitSuicide(false, true);
        }
        UpdateAllTags();
    }

    void Match()
    {
        if (_arenas.Count == 0) return; // map not set up yet
        CheckSpawns();
        CheckRefills();
        _waiting.RemoveAll(w => !IsPlaying(w.Player));
        if (_waiting.Count < 2) return;
        // 4+: shuffle everyone. 3: the last loser sits out, so the winner plays the one who waited.
        var candidates = _waiting.Count == 3 ? _waiting.Where(w => w.Player != _lastLoser).ToList() : [.. _waiting];

        foreach (var (a, b) in Pair(candidates))
        {
            // Two who just fought each other only get a rematch once no other duel is running (then there's
            // nobody else to shuffle them with)
            if (_waiting.Count < 4 && _duels.Count > 0 && IsRematch(a, b)) continue;
            var arena = a.Arena ?? b.Arena ?? FreeArenas().OrderBy(_ => Random.Shared.Next()).FirstOrDefault();
            if (arena == null)
            {
                Logger.LogWarning("BoelkArena: no free arena for {A} vs {B}", a.Player.PlayerName, b.Player.PlayerName);
                break;
            }
            _waiting.Remove(a);
            _waiting.Remove(b);
            StartDuel(arena, a.Player, b.Player);
        }
        UpdateAllTags();
    }

    // A duel player is still dead after the duel started (respawn didn't work): try again, and after 5 seconds give up on
    // the duel (both back to waiting, unrated) instead of leaving the other one standing alone forever
    void CheckSpawns()
    {
        foreach (var duel in _duels.ToList())
        {
            // (A duel player who dies ends the duel, so a dead one here was never spawned)
            var missing = new[] { duel.T, duel.Ct }.Where(p => p.IsValid && !IsAlive(p)).ToList();
            if (missing.Count == 0 || Server.CurrentTime - duel.StartedAt < 1.5f) continue;
            if (Server.CurrentTime - duel.StartedAt < 5f)
            {
                // Once only: respawning a player who is in fact alive breaks their model (invisible player)
                if (!duel.RespawnRetried)
                {
                    duel.RespawnRetried = true;
                    missing.ForEach(p => { _pendingSetup.Add(p.Slot); p.Respawn(); });
                }
                continue;
            }
            Logger.LogWarning("BoelkArena: could not respawn {Players} for their duel, cancelling it", string.Join(", ", missing.Select(p => p.PlayerName)));
            _duels.Remove(duel);
            foreach (var p in new[] { duel.T, duel.Ct }.Where(IsPlaying))
            {
                _pendingSetup.Remove(p.Slot);
                _waiting.Add(new Waiter(p, IsAlive(p) ? duel.Arena : null));
            }
        }
    }

    // Random pairs; of a few shuffles, the one with the fewest rematches of the last duel
    List<(Waiter, Waiter)> Pair(List<Waiter> players)
    {
        List<(Waiter, Waiter)> best = [];
        var bestRematches = int.MaxValue;
        for (var attempt = 0; attempt < 10 && bestRematches > 0; attempt++)
        {
            var shuffled = players.OrderBy(_ => Random.Shared.Next()).ToList();
            var pairs = Enumerable.Range(0, shuffled.Count / 2).Select(i => (shuffled[2 * i], shuffled[2 * i + 1])).ToList();
            var rematches = pairs.Count(p => IsRematch(p.Item1, p.Item2));
            if (rematches < bestRematches) (best, bestRematches) = (pairs, rematches);
        }
        return best;
    }

    bool IsRematch(Waiter a, Waiter b) => _lastOpponent.GetValueOrDefault(a.Player.Slot, -1) == b.Player.Slot;

    // Refill rounds (grenades, ...): checked every second; a duel player without the round's weapon gets it
    // back RefillSeconds after it went missing. (The taser stays in the hand after firing, see OnWeaponFire.)
    readonly Dictionary<int, float> _missingSince = new(); // player slot -> time the weapon was first missing

    void CheckRefills()
    {
        foreach (var duel in _duels)
        {
            if (!duel.Type.Refill || duel.Weapon is not { } weapon) continue;
            foreach (var p in new[] { duel.T, duel.Ct })
            {
                if (!p.IsValid || !IsAlive(p) || _pendingSetup.Contains(p.Slot)) continue;
                var has = p.PlayerPawn.Value!.WeaponServices?.MyWeapons.Any(w => w.Value?.DesignerName == weapon) ?? true;
                if (has)
                    _missingSince.Remove(p.Slot);
                else if (!_missingSince.TryGetValue(p.Slot, out var since))
                    _missingSince[p.Slot] = Server.CurrentTime;
                else if (Server.CurrentTime - since >= duel.Type.RefillSeconds)
                {
                    _missingSince.Remove(p.Slot);
                    p.GiveNamedItem(weapon);
                }
            }
        }
    }

    IEnumerable<Arena> FreeArenas() =>
        _arenas.Where(a => _duels.All(d => d.Arena != a) && _waiting.All(w => w.Arena != a));

    void StartDuel(Arena arena, CCSPlayerController t, CCSPlayerController ct)
    {
        var type = PickRoundType(t, ct);
        var duel = new Duel(arena, t, ct, type, Pick(type.Weapons), Pick(type.Secondary));
        _duels.Add(duel);
        _lastOpponent[t.Slot] = ct.Slot;
        _lastOpponent[ct.Slot] = t.Slot;

        foreach (var (p, team) in new[] { (t, CsTeam.Terrorist), (ct, CsTeam.CounterTerrorist) })
        {
            if (p.Team != team) p.SwitchTeam(team); // SwitchTeam keeps an alive player alive
            if (IsAlive(p))
                SetupPlayer(p, duel);
            else
            {
                // Respawn a moment after the team switch: in the same frame it doesn't take effect yet
                _pendingSetup.Add(p.Slot);
                AddTimer(0.5f, () => { if (p.IsValid && !IsAlive(p) && DuelOf(p) == duel) p.Respawn(); }, TimerFlags.STOP_ON_MAPCHANGE);
            }
            var weapon = type.Custom ? $"{Short(CustomWeapon(p))} - change with !weapon" : Short(duel.Weapon);
            p.PrintToChat($" {ChatColors.Green}[Arena {arena.Id}]{ChatColors.Default} vs {ChatColors.Gold}{duel.Other(p).PlayerName}{ChatColors.Default} - {type.Name} ({weapon})");
        }
    }

    static string? Pick(List<string> list) => list.Count == 0 ? null : list[Random.Shared.Next(list.Count)];

    // Teleport into the arena, full health/armor, the duel's weapons
    void SetupPlayer(CCSPlayerController p, Duel duel)
    {
        var pawn = p.PlayerPawn.Value;
        if (pawn == null) return;
        _missingSince.Remove(p.Slot);
        var spawns = p == duel.T ? duel.Arena.TSpawns : duel.Arena.CtSpawns;
        var spawn = spawns[Random.Shared.Next(spawns.Count)];
        pawn.Teleport(spawn.AbsOrigin, spawn.AbsRotation, new Vector(0, 0, 0));
        ResetPlayer(p, duel.Type.Armor, duel.Type.Helmet);
        SetVisible(pawn, !duel.Type.Invisible);
        p.RemoveWeapons();
        var weapon = duel.Type.Custom ? CustomWeapon(p) : duel.Weapon;
        if (duel.Type.Knife || weapon == null) p.GiveNamedItem("weapon_knife");
        if (duel.Secondary != null) p.GiveNamedItem(duel.Secondary);
        if (weapon != null) p.GiveNamedItem(weapon);
    }

    static void SetVisible(CCSPlayerPawn pawn, bool visible)
    {
        pawn.Render = Color.FromArgb(visible ? 255 : 0, 255, 255, 255);
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
    }

    void ResetPlayer(CCSPlayerController p, bool armor, bool helmet)
    {
        var pawn = p.PlayerPawn.Value;
        if (pawn == null) return;
        pawn.Health = 100;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        Server.NextWorldUpdate(() =>
        {
            if (!p.IsValid || p.PlayerPawn.Value is not { } pw) return;
            pw.ArmorValue = armor ? 100 : 0;
            Utilities.SetStateChanged(pw, "CCSPlayerPawn", "m_ArmorValue");
            if (pw.ItemServices != null)
            {
                _ = new CCSPlayer_ItemServices(pw.ItemServices.Handle) { HasHelmet = helmet };
                Utilities.SetStateChanged(pw, "CBasePlayerPawn", "m_pItemServices");
            }
        });
    }

    // A duel is over: the winner waits alive (full health) in the arena, the loser waits dead
    void EndDuel(Duel duel, CCSPlayerController loser, bool rated)
    {
        _duels.Remove(duel);
        var winner = duel.Other(loser);
        if (IsPlaying(winner))
        {
            _waiting.Add(new Waiter(winner, IsAlive(winner) ? duel.Arena : null));
            if (IsAlive(winner)) ResetPlayer(winner, duel.Type.Armor, duel.Type.Helmet);
        }
        if (IsPlaying(loser)) _waiting.Add(new Waiter(loser, null));
        _lastLoser = loser;
        if (rated) ReportDuel(winner, loser, duel.Arena);
        UpdateAllTags();
    }

    // Player gone or spectating: their duel ends unrated, they stop waiting
    void Leave(CCSPlayerController p)
    {
        _pendingSetup.Remove(p.Slot);
        if (DuelOf(p) is { } duel) EndDuel(duel, p, rated: false);
        _waiting.RemoveAll(w => w.Player == p);
    }

    // ---------------------------------------------------------------- game events

    // A new CS2 round (only after mp_restartgame or the 60 min round time): start over
    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart e, GameEventInfo info)
    {
        AddTimer(0.2f, Restart, TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn e, GameEventInfo info)
    {
        var p = e.Userid;
        if (p is not { IsValid: true }) return HookResult.Continue;
        Server.NextFrame(() =>
        {
            if (!p.IsValid) return;
            if (_pendingSetup.Remove(p.Slot) && DuelOf(p) is { } duel)
                SetupPlayer(p, duel);
            else if (DuelOf(p) == null && IsAlive(p))
                p.PlayerPawn.Value!.CommitSuicide(false, true); // spawned outside a duel (team join)
        });
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath e, GameEventInfo info)
    {
        var victim = e.Userid;
        if (victim is not { IsValid: true }) return HookResult.Continue;
        if (DuelOf(victim) is { } duel)
            EndDuel(duel, victim, rated: true);
        else if (_waiting.FirstOrDefault(w => w.Player == victim) is { } w)
            w.Arena = null; // a waiting winner died somehow: their arena is free again
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerTeam(EventPlayerTeam e, GameEventInfo info)
    {
        var p = e.Userid;
        if (p is not { IsValid: true } || p.IsHLTV || e.Disconnect) return HookResult.Continue;
        var team = (CsTeam)e.Team;
        Server.NextFrame(() =>
        {
            if (!p.IsValid) return;
            if (team <= CsTeam.Spectator)
                Leave(p);
            else if (DuelOf(p) == null && _waiting.All(w => w.Player != p))
            {
                _waiting.Add(new Waiter(p, null));
                UpdateAllTags();
            }
        });
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull e, GameEventInfo info)
    {
        var p = e.Userid;
        UpdateBotQuota();
        // Put new players straight into a team, so they join the waiting list
        AddTimer(1f, () =>
        {
            if (p is { IsValid: true, IsHLTV: false } && p.Team == CsTeam.None) p.ChangeTeam(CsTeam.Terrorist);
        }, TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect e, GameEventInfo info)
    {
        if (e.Userid is { IsValid: true } p) Leave(p);
        AddTimer(0.1f, UpdateBotQuota, TimerFlags.STOP_ON_MAPCHANGE); // still listed during the event
        return HookResult.Continue;
    }

    // Grenade refill: as soon as the thrown grenade goes off. (The every-second check in CheckRefills alone
    // missed molotovs: a thrown one can stay listed in the inventory.)
    [GameEventHandler]
    public HookResult OnHeDetonate(EventHegrenadeDetonate e, GameEventInfo info) { RefillNow(e.Userid); return HookResult.Continue; }

    [GameEventHandler]
    public HookResult OnMolotovDetonate(EventMolotovDetonate e, GameEventInfo info) { RefillNow(e.Userid); return HookResult.Continue; }

    void RefillNow(CCSPlayerController? p)
    {
        if (p is not { IsValid: true } || !IsAlive(p) || DuelOf(p) is not { Type.Refill: true, Weapon: { } weapon }) return;
        _missingSince.Remove(p.Slot);
        p.RemoveItemByDesignerName(weapon); // a thrown one may still be listed
        p.GiveNamedItem(weapon);
    }

    // Taser refill: a fired taser stays in the hand (recharging), so CheckRefills can't see it's used up -
    // replace it with a fresh one RefillSeconds after each shot
    [GameEventHandler]
    public HookResult OnWeaponFire(EventWeaponFire e, GameEventInfo info)
    {
        var p = e.Userid;
        if (e.Weapon != "weapon_taser" || p is not { IsValid: true }) return HookResult.Continue;
        if (DuelOf(p) is not { Type.Refill: true, Weapon: "weapon_taser" } duel) return HookResult.Continue;
        const string duelWeapon = "weapon_taser";
        AddTimer(duel.Type.RefillSeconds, () =>
        {
            if (!p.IsValid || DuelOf(p) != duel || !IsAlive(p)) return;
            p.RemoveItemByDesignerName(duelWeapon);
            p.GiveNamedItem(duelWeapon);
        }, TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    // ---------------------------------------------------------------- round type preferences (!rounds)

    // SteamID64 -> round type name -> enabled; players without a choice get EnabledByDefault
    Dictionary<string, Dictionary<string, bool>> _roundPrefs = new();
    string RoundPrefsFile => Path.Combine(ModuleDirectory, "round-prefs.json");

    void LoadRoundPrefs()
    {
        try
        {
            if (File.Exists(RoundPrefsFile))
                _roundPrefs = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, bool>>>(File.ReadAllText(RoundPrefsFile)) ?? new();
            if (File.Exists(CustomWeaponsFile))
                _customWeapons = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CustomWeaponsFile)) ?? new();
        }
        catch (Exception ex) { Logger.LogWarning("BoelkArena: could not read {File}: {Error}", RoundPrefsFile, ex.Message); }
    }

    bool Enabled(CCSPlayerController p, RoundType type) =>
        p.IsBot ? type.EnabledByDefault
        : _roundPrefs.GetValueOrDefault(p.SteamID.ToString())?.GetValueOrDefault(type.Name, type.EnabledByDefault) ?? type.EnabledByDefault;

    // Random round type both players have enabled; if they have none in common, any default one
    RoundType PickRoundType(CCSPlayerController a, CCSPlayerController b)
    {
        var common = Config.RoundTypes.Where(t => Enabled(a, t) && Enabled(b, t)).ToList();
        if (common.Count == 0) common = Config.RoundTypes.Where(t => t.EnabledByDefault).ToList();
        if (common.Count == 0) common = Config.RoundTypes;
        return common.Count == 0 ? new RoundType { Name = "Knife" } : common[Random.Shared.Next(common.Count)];
    }

    // Center-screen menu: redrawn every tick, so toggles show instantly (a chat menu prints ~10 lines each
    // time, which CS2 shows one after another), and it stays on its page after a selection.
    [ConsoleCommand("css_rounds", "Choose which arena round types you want to play")]
    [ConsoleCommand("css_guns", "Choose which arena round types you want to play")]
    [ConsoleCommand("css_round", "Choose which arena round types you want to play")]
    public void OnRounds(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller is not { IsValid: true, IsBot: false }) return;
        static string Label(bool on, RoundType type) =>
            $"<font color='{(on ? "lime" : "grey")}'>{(on ? "ON" : "OFF")}</font> {type.Name}";
        var menu = new CenterHtmlMenu("Round types (!close)", this) { PostSelectAction = PostSelectAction.Nothing };
        foreach (var type in Config.RoundTypes)
        {
            menu.AddMenuOption(Label(Enabled(caller, type), type), (player, option) =>
            {
                var key = player.SteamID.ToString();
                if (!_roundPrefs.TryGetValue(key, out var prefs)) _roundPrefs[key] = prefs = new();
                var on = !Enabled(player, type);
                prefs[type.Name] = on;
                File.WriteAllText(RoundPrefsFile, JsonSerializer.Serialize(_roundPrefs));
                option.Text = Label(on, type);
            });
        }
        menu.Open(caller);
    }

    // ---------------------------------------------------------------- custom weapon (!weapon)

    static readonly (string Category, string[] Weapons)[] WeaponCategories =
    [
        ("Rifles", ["weapon_ak47", "weapon_m4a1_silencer", "weapon_m4a1", "weapon_galilar", "weapon_famas", "weapon_sg556", "weapon_aug"]),
        ("Snipers", ["weapon_awp", "weapon_ssg08", "weapon_scar20", "weapon_g3sg1"]),
        ("SMGs", ["weapon_mac10", "weapon_mp9", "weapon_mp7", "weapon_p90", "weapon_mp5sd", "weapon_bizon", "weapon_ump45"]),
        ("Shotguns", ["weapon_xm1014", "weapon_nova", "weapon_mag7", "weapon_sawedoff"]),
        ("Heavy", ["weapon_m249", "weapon_negev"]),
        ("Pistols", ["weapon_deagle", "weapon_glock", "weapon_usp_silencer", "weapon_hkp2000", "weapon_elite", "weapon_tec9", "weapon_p250", "weapon_cz75a", "weapon_fiveseven", "weapon_revolver"]),
        ("Other", ["weapon_knife", "weapon_taser", "weapon_hegrenade", "weapon_molotov"]),
    ];

    // SteamID64 -> weapon for "Custom" rounds; players without a pick get a random rifle
    Dictionary<string, string> _customWeapons = new();
    string CustomWeaponsFile => Path.Combine(ModuleDirectory, "custom-weapons.json");

    string CustomWeapon(CCSPlayerController p) =>
        _customWeapons.GetValueOrDefault(p.SteamID.ToString()) ?? Pick(WeaponCategories[0].Weapons.ToList())!;

    static string Short(string? weapon) => weapon?.Replace("weapon_", "") ?? "knife";

    [ConsoleCommand("css_weapon", "Choose your weapon for Custom arena rounds")]
    [ConsoleCommand("css_custom", "Choose your weapon for Custom arena rounds")]
    public void OnWeapon(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller is not { IsValid: true, IsBot: false }) return;
        var categories = new CenterHtmlMenu($"Custom weapon: {Short(CustomWeapon(caller))} (!close)", this);
        foreach (var (category, weapons) in WeaponCategories)
            categories.AddMenuOption(category, (player, _) =>
            {
                var menu = new CenterHtmlMenu($"{category} (!close)", this) { PostSelectAction = PostSelectAction.Close };
                foreach (var weapon in weapons)
                    menu.AddMenuOption(Short(weapon), (pl, _) =>
                    {
                        _customWeapons[pl.SteamID.ToString()] = weapon;
                        File.WriteAllText(CustomWeaponsFile, JsonSerializer.Serialize(_customWeapons));
                        pl.PrintToChat($" {ChatColors.Green}[Arena]{ChatColors.Default} Custom rounds: {ChatColors.Gold}{Short(weapon)}");
                    });
                menu.Open(player);
            });
        categories.Open(caller);
    }

    // !kill: suicide. In a duel that counts as a loss (the opponent wins, rated).
    [ConsoleCommand("css_kill", "Kills yourself")]
    public void OnKill(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller is { IsValid: true } && IsAlive(caller)) caller.PlayerPawn.Value!.CommitSuicide(false, true);
    }

    [ConsoleCommand("css_close", "Closes the open menu")]
    public void OnClose(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller is { IsValid: true }) MenuManager.GetActiveMenu(caller)?.Close();
    }

    // ---------------------------------------------------------------- backend: ELO reports, tags, settings

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    readonly CancellationTokenSource _stop = new();

    // Player key (SteamID64, bots "bot:<name>", see backend elo.ts playerKey) -> tag text. Replaced as a
    // whole on every state received (so a reset in the frontend clears it); written by the background
    // listener and HTTP callbacks, read on the main thread.
    volatile ConcurrentDictionary<string, string> _tags = new();
    volatile string _defaultTag = "1000"; // players without a rating yet
    volatile bool _fillBot;

    static string Key(CCSPlayerController p) => p.IsBot ? $"bot:{p.PlayerName}" : p.SteamID.ToString();

    // The duel webhook is http://<backend>/api/instances/<name>/duel-events; the ranking lives at
    // <backend>/api/elo, the push socket at ws://<backend>/ws/events
    string? Backend => Config.Url.IndexOf("/api/", StringComparison.Ordinal) is var i and >= 0 ? Config.Url[..i] : null;

    void ReportDuel(CCSPlayerController winner, CCSPlayerController loser, Arena arena)
    {
        if (string.IsNullOrWhiteSpace(Config.Url) || !winner.IsValid || !loser.IsValid) return;
        var body = new
        {
            @event = "duel",
            arena = $"ARENA {arena.Id}",
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
    }

    async Task ListenForUpdates(string backend)
    {
        var eventsUrl = new Uri(System.Text.RegularExpressions.Regex.Replace(backend, "^http", "ws") + "/ws/events");
        var buffer = new byte[64 * 1024];
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(eventsUrl, _stop.Token);
                Apply(await Http.GetFromJsonAsync<EloResponse>(backend + "/api/elo", _stop.Token)); // catch up
                while (socket.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult part;
                    do
                    {
                        part = await socket.ReceiveAsync(buffer, _stop.Token);
                        message.Write(buffer, 0, part.Count);
                    } while (!part.EndOfMessage);
                    if (part.MessageType == WebSocketMessageType.Close) break;

                    using var doc = JsonDocument.Parse(message.ToArray());
                    if (doc.RootElement.TryGetProperty("type", out var type) && type.GetString() == "elo")
                        Apply(doc.RootElement.GetProperty("data").Deserialize<EloResponse>());
                }
            }
            catch { } // backend down or restarted: keep the last tags and settings, reconnect
            try { await Task.Delay(2000, _stop.Token); } catch (OperationCanceledException) { }
        }
    }

    void Apply(EloResponse? state)
    {
        if (state == null) return;
        _tags = new(state.Ranking.ToDictionary(p => p.SteamId, p => p.Tag));
        _defaultTag = state.DefaultTag;
        _fillBot = state.Settings.FillBot;
        Server.NextFrame(() => { UpdateAllTags(); UpdateBotQuota(); });
    }

    void UpdateAllTags()
    {
        foreach (var player in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsHLTV: false }))
        {
            var where = DuelOf(player) is { } duel ? $"ARENA {duel.Arena.Id}" : "WAIT";
            var tag = $"{where} | {_tags.GetValueOrDefault(Key(player), _defaultTag)}";
            if (player.Clan == tag) continue;
            player.Clan = tag;
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_szClan");
        }
    }

    // Prints every player's current clan tag and duel to the server console (for checking)
    [ConsoleCommand("css_arena_tags", "Lists the arena/ELO clan tags and duels of all players")]
    public void OnArenaTags(CCSPlayerController? caller, CommandInfo info)
    {
        foreach (var p in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsHLTV: false }))
            info.ReplyToCommand($"[{p.Clan}] {p.PlayerName}{(DuelOf(p) is { } d ? $" vs {d.Other(p).PlayerName} ({d.Type.Name})" : "")}");
        info.ReplyToCommand($"{_arenas.Count} arenas, {_duels.Count} duels, {_waiting.Count} waiting");
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

    record RankingEntry([property: JsonPropertyName("steamId")] string SteamId, [property: JsonPropertyName("tag")] string Tag);
    record Settings([property: JsonPropertyName("fillBot")] bool FillBot);
    record EloResponse(
        [property: JsonPropertyName("ranking")] List<RankingEntry> Ranking,
        [property: JsonPropertyName("settings")] Settings Settings,
        [property: JsonPropertyName("defaultTag")] string DefaultTag);
    record DuelResponse([property: JsonPropertyName("tags")] Dictionary<string, string> Tags);
}
