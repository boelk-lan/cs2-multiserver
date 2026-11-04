// Finds the arenas on an arena map (am_*): groups the map's CT and T spawn points into clusters that are
// close to each other. Same approach as K4-Arenas' ArenaFinder (https://github.com/K4ryuu/K4-Arenas),
// written from scratch: a CT and a T spawn closer than ~1.1x the median distance from a spawn to its
// nearest enemy spawn belong to the same arena, then clusters whose centres are close are merged.
// ponytail: no support for maps that use info_teleport_destination instead of spawns (K4's "CYBERSHOKE"
// mode); add it if such a map is ever used.
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace BoelkArena;

// All T and CT spawns of the arena; each duel player starts on a random one of their side
public record Arena(int Id, List<SpawnPoint> TSpawns, List<SpawnPoint> CtSpawns);

public static class ArenaFinder
{
    const float Factor = 1.1f;
    const float MergeFactor = 1.5f;

    public static List<Arena> Find()
    {
        var spawns = Utilities.FindAllEntitiesByDesignerName<SpawnPoint>("info_player_counterterrorist")
            .Concat(Utilities.FindAllEntitiesByDesignerName<SpawnPoint>("info_player_terrorist"))
            .Where(s => s.AbsOrigin != null)
            .ToList();
        static bool IsCt(SpawnPoint s) => s.DesignerName == "info_player_counterterrorist";
        if (!spawns.Any(IsCt) || spawns.All(IsCt)) return [];

        var enemyDistances = spawns
            .Select(s => spawns.Where(o => IsCt(o) != IsCt(s)).Min(o => Distance(s.AbsOrigin!, o.AbsOrigin!)))
            .OrderBy(d => d)
            .ToList();
        var threshold = enemyDistances[enemyDistances.Count / 2] * Factor;

        // Union-find over spawns: a CT and a T spawn within the threshold are in the same arena
        var parent = spawns.ToDictionary(s => s, s => s);
        SpawnPoint Root(SpawnPoint s) => parent[s] == s ? s : parent[s] = Root(parent[s]);
        foreach (var ct in spawns.Where(IsCt))
            foreach (var t in spawns.Where(s => !IsCt(s)))
                if (Distance(ct.AbsOrigin!, t.AbsOrigin!) <= threshold)
                    parent[Root(t)] = Root(ct);

        var clusters = spawns.GroupBy(Root).Select(g => g.ToList()).ToList();
        for (var merged = true; merged;)
        {
            merged = false;
            for (var i = 0; i < clusters.Count && !merged; i++)
                for (var j = i + 1; j < clusters.Count && !merged; j++)
                    if (Distance(Centre(clusters[i]), Centre(clusters[j])) < threshold * MergeFactor)
                    {
                        clusters[i].AddRange(clusters[j]);
                        clusters.RemoveAt(j);
                        merged = true;
                    }
        }

        return clusters
            .Where(c => c.Any(IsCt) && !c.All(IsCt))
            .Select((c, i) => new Arena(i + 1, c.Where(s => !IsCt(s)).ToList(), c.Where(IsCt).ToList()))
            .ToList();
    }

    static float Distance(Vector a, Vector b) =>
        MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    static Vector Centre(List<SpawnPoint> c) =>
        new(c.Average(s => s.AbsOrigin!.X), c.Average(s => s.AbsOrigin!.Y), c.Average(s => s.AbsOrigin!.Z));
}
