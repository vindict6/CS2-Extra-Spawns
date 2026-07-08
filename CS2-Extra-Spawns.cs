using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using CSTimer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace CS2_Extra_Spawns
{
    [MinimumApiVersion(80)]
    public class CS2_Extra_Spawns : BasePlugin
    {
        public override string ModuleName => "CS2-Extra-Spawns";
        public override string ModuleVersion => "1.2.0";
        public override string ModuleAuthor => "|ZAPS| BONE";

        private const int MaxSpawnsPerTeam = 32;

        // How far the offset pattern is allowed to expand outward (ring 1 = the
        // original ±5 unit pattern, ring 2 = ±10, and so on). Replaces the previous
        // accidental behavior where offsets chained off already-offset spawns,
        // gaining +5 Z per generation and slowly drifting spawn points into the air.
        private const int MaxOffsetRings = 8;

        // Horizontal offset pattern. Z is handled separately and stays a constant
        // +5 above the ORIGINAL spawn height for every ring.
        private static readonly (float X, float Y)[] Offsets =
        {
            (5f, 0f),
            (-5f, 0f),
            (0f, 5f),
            (0f, -5f)
        };

        // Plain-float snapshot of each ORIGINAL map spawn, captured once per map.
        // Deliberately not stored as Vector/QAngle: those wrap native engine memory,
        // and holding them across rounds/entity churn is a stale-pointer crash risk.
        private sealed record SpawnPoint(float X, float Y, float Z, float RotX, float RotY, float RotZ);

        private readonly Dictionary<string, List<SpawnPoint>> _originalSpawns = new()
        {
            ["info_player_terrorist"] = new List<SpawnPoint>(),
            ["info_player_counterterrorist"] = new List<SpawnPoint>(),
        };

        private CSTimer? _solidTeammatesTimer;

        // True from the moment we set mp_solid_teammates to 0 until we've restored
        // it. Guards the capture below so we can never record our own 0 as the
        // server's "original" value.
        private bool _solidRestorePending = false;
        private int _originalSolidTeammates = 2;

        public override void Load(bool hotReload)
        {
            RegisterListener<Listeners.OnMapStart>(OnMapStart);
            RegisterEventHandler<EventRoundStart>(OnRoundStart);

            AddCommand("css_spawns", "Generate extra spawns for T and CT", (player, info) =>
            {
                // Server console only. css_ commands are also reachable from player
                // chat (!spawns), and unauthenticated players should not be able to
                // trigger entity creation.
                if (player != null)
                {
                    info.ReplyToCommand("[ExtraSpawns] This command can only be used from the server console.");
                    return;
                }

                int t = AddExtraSpawns("info_player_terrorist");
                int ct = AddExtraSpawns("info_player_counterterrorist");
                info.ReplyToCommand($"[ExtraSpawns] Extra spawns generated (T: +{t}, CT: +{ct}).");
            });
        }

        public override void Unload(bool hotReload)
        {
            // If we're mid-window, put the cvar back before we disappear. Previously
            // an unload inside the 5-second window left mp_solid_teammates at 0
            // permanently.
            if (_solidRestorePending)
            {
                try { Server.ExecuteCommand($"mp_solid_teammates {_originalSolidTeammates}"); } catch { }
                _solidRestorePending = false;
            }

            _solidTeammatesTimer?.Kill();
            _solidTeammatesTimer = null;

            DeregisterEventHandler<EventRoundStart>(OnRoundStart);
            RemoveListener<Listeners.OnMapStart>(OnMapStart);
        }

        private void OnMapStart(string map)
        {
            // New map: the old snapshot is invalid.
            foreach (var list in _originalSpawns.Values) list.Clear();

            // Deliberately do NOT reset _solidRestorePending here. If the map changed
            // inside the 5-second window, the restore timer was killed
            // (STOP_ON_MAPCHANGE) and the cvar is STILL 0 — the pending flag is what
            // makes the next round restore the true original instead of capturing
            // that leftover 0 as the server's configured value.

            // Delay to let map entities load before first round
            AddTimer(1.0f, () =>
            {
                CaptureOriginalSpawns("info_player_terrorist");
                CaptureOriginalSpawns("info_player_counterterrorist");
                AddExtraSpawns("info_player_terrorist");
                AddExtraSpawns("info_player_counterterrorist");
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }

        private HookResult OnRoundStart(EventRoundStart ev, GameEventInfo info)
        {
            _solidTeammatesTimer?.Kill();
            _solidTeammatesTimer = null;

            // Recreate spawns every round -- CS2 can clean up dynamic entities between rounds
            AddExtraSpawns("info_player_terrorist");
            AddExtraSpawns("info_player_counterterrorist");

            // Capture the server's real configured value BEFORE we touch it — but only
            // if no restore is pending (if one is, the cvar currently holds our 0).
            // Previously the restore hardcoded "2", silently overriding servers
            // configured for 0 or 1 on every single round.
            if (!_solidRestorePending)
            {
                _originalSolidTeammates = ReadSolidTeammates() ?? _originalSolidTeammates;
            }

            // Force collisions OFF so overlapping players can separate
            _solidRestorePending = true;
            Server.ExecuteCommand("mp_solid_teammates 0");
            Server.PrintToConsole("[ExtraSpawns] mp_solid_teammates set to 0 at round start.");

            // Restore collisions after 5 seconds
            _solidTeammatesTimer = AddTimer(5.0f, () =>
            {
                Server.ExecuteCommand($"mp_solid_teammates {_originalSolidTeammates}");
                Server.PrintToConsole($"[ExtraSpawns] mp_solid_teammates restored to {_originalSolidTeammates}.");
                _solidRestorePending = false;
                _solidTeammatesTimer = null;
            }, TimerFlags.STOP_ON_MAPCHANGE);

            return HookResult.Continue;
        }

        private static int? ReadSolidTeammates()
        {
            try
            {
                var cvar = ConVar.Find("mp_solid_teammates");
                return cvar?.GetPrimitiveValue<int>();
            }
            catch
            {
                // Cvar type mismatch or unavailable: fall back to the last known
                // value (initially 2, matching the plugin's previous behavior).
                return null;
            }
        }

        private void CaptureOriginalSpawns(string className)
        {
            var list = _originalSpawns[className];
            if (list.Count > 0) return; // already captured for this map

            try
            {
                foreach (var spawn in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(className))
                {
                    var origin = spawn.AbsOrigin;
                    var angles = spawn.AbsRotation;
                    if (origin == null || angles == null) continue;

                    // Copy the floats out immediately; never hold the native structs.
                    list.Add(new SpawnPoint(origin.X, origin.Y, origin.Z, angles.X, angles.Y, angles.Z));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ExtraSpawns] Failed to capture original spawns for {className}: {ex.Message}");
            }
        }

        private int AddExtraSpawns(string className)
        {
            try
            {
                var liveSpawns = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(className).ToList();
                if (liveSpawns.Count == 0)
                    return 0;

                // Lazy capture covers hot reloads mid-map, where OnMapStart never
                // fired for this plugin instance. (In that case previously created
                // dynamic spawns are captured as bases too — they're valid, in-bounds
                // spawn positions, so that's acceptable.)
                CaptureOriginalSpawns(className);
                var bases = _originalSpawns[className];
                if (bases.Count == 0)
                    return 0;

                int needed = MaxSpawnsPerTeam - liveSpawns.Count;
                if (needed <= 0)
                    return 0;

                // Positions that already have a spawn point, rounded to whole units.
                // Skipping occupied candidates fixes the old stacking behavior where
                // regenerated offsets landed exactly on surviving dynamic spawns,
                // producing multiple spawn points at the identical position.
                var occupied = new HashSet<(int, int, int)>();
                foreach (var spawn in liveSpawns)
                {
                    var o = spawn.AbsOrigin;
                    if (o == null) continue;
                    occupied.Add(((int)MathF.Round(o.X), (int)MathF.Round(o.Y), (int)MathF.Round(o.Z)));
                }

                int created = 0;

                // Ring 1 reproduces the original ±5 unit pattern exactly; further
                // rings expand outward horizontally only, keeping Z at a constant +5
                // above the original spawn height.
                for (int ring = 1; ring <= MaxOffsetRings && created < needed; ring++)
                {
                    foreach (var b in bases)
                    {
                        if (created >= needed) break;

                        foreach (var (ox, oy) in Offsets)
                        {
                            if (created >= needed) break;

                            float x = b.X + ox * ring;
                            float y = b.Y + oy * ring;
                            float z = b.Z + 5f;

                            var key = ((int)MathF.Round(x), (int)MathF.Round(y), (int)MathF.Round(z));
                            if (!occupied.Add(key)) continue; // a spawn already sits here

                            var newSpawn = Utilities.CreateEntityByName<CBaseEntity>(className);
                            if (newSpawn == null) continue;

                            newSpawn.Teleport(new Vector(x, y, z), new QAngle(b.RotX, b.RotY, b.RotZ), null);
                            newSpawn.DispatchSpawn();
                            created++;
                        }
                    }
                }

                if (created > 0)
                    Server.PrintToConsole($"[ExtraSpawns] Added {created} spawns for {className}.");

                return created;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ExtraSpawns] AddExtraSpawns error for {className} (possibly mid-transition): {ex.Message}");
                return 0;
            }
        }
    }
}
