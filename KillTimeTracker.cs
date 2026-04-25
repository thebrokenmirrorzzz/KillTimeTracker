using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Trace;
using SwiftlyS2.Shared.Scheduler;

namespace KillTimeTracker;

[PluginMetadata(
    Id = "KillTimeTracker",
    Version = "1.0.0",
    Name = "Kill Time Tracker",
    Author = "thebrokenmirror",
    Description = "击杀计时器，记录玩家击杀时间。"
)]
public sealed class KillTimeTracker : BasePlugin
{
    private const double MAX_ELAPSED_MS = 9999.0;
    private const double SIGHT_LOST_TIMEOUT_MS = 3000.0;

    private static readonly Vector HullMins = new(-3, -3, -3);
    private static readonly Vector HullMaxs = new(3, 3, 3);

    private readonly Dictionary<int, TargetState> playerTargets = new();
    private readonly Dictionary<int, Dictionary<int, DamageEntry>> playerDamageTrack = new();
    private CancellationTokenSource? detectionCts;

    public KillTimeTracker(ISwiftlyCore core) : base(core)
    {
    }

    public override void Load(bool hotReload)
    {
        detectionCts = Core.Scheduler.AddTimer(ctx =>
        {
            DetectionTick();
            return TimerStep.WaitForMilliseconds(100);
        });
        Core.Event.OnClientDisconnected += OnClientDisconnected;

        Core.Logger.LogInformation("[KillTimeTracker] Plugin loaded successfully.");
    }

    public override void Unload()
    {
        detectionCts?.Cancel();
        Core.Event.OnClientDisconnected -= OnClientDisconnected;

        Core.Logger.LogInformation("[KillTimeTracker] Plugin unloaded.");
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        var player = Core.PlayerManager.GetPlayer(@event.PlayerId);
        if (player == null) return;

        playerTargets.Remove(player.Slot);
        playerDamageTrack.Remove(player.Slot);
    }

    private void CleanDamageTrack(int attackerSlot)
    {
        if (!playerDamageTrack.TryGetValue(attackerSlot, out var track)) return;

        var now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerMillisecond;
        var expired = new List<int>(track.Count);
        foreach (var kv in track)
        {
            if (now - kv.Value.LastHitTime > SIGHT_LOST_TIMEOUT_MS)
                expired.Add(kv.Key);
        }
        foreach (var victimSlot in expired)
            track.Remove(victimSlot);

        if (track.Count == 0)
            playerDamageTrack.Remove(attackerSlot);
    }

    private HashSet<int> DetectVisible(IEnumerable<IPlayer> allPlayers, IPlayer viewer)
    {
        var visible = new HashSet<int>();
        var pawn = viewer.PlayerPawn;
        if (pawn?.IsValid != true) return visible;

        Vector? eyePosNullable;
        QAngle eyeAngles;
        try
        {
            eyePosNullable = pawn.EyePosition;
            if (eyePosNullable == null) return visible;
            eyeAngles = pawn.EyeAngles;
        }
        catch
        {
            return visible;
        }

        var eyePos = eyePosNullable.Value;
        eyeAngles.ToDirectionVectors(out var forward, out var _, out var _);

        var hullParams = TraceParams.Builder()
            .WithHullRay(HullMins, HullMaxs)
            .InteractWith(MaskTrace.Player)
            .InteractExclude(MaskTrace.Trigger)
            .IgnoreEntity(pawn)
            .Build();

        foreach (var other in allPlayers)
        {
            if (other?.IsValid != true || !other.IsAlive) continue;
            if (other.Slot == viewer.Slot) continue;

            var otherPawn = other.PlayerPawn;
            if (otherPawn?.IsValid != true) continue;

            Vector otherOrigin;
            try
            {
                var absOrigin = otherPawn.AbsOrigin;
                if (absOrigin == null) continue;
                otherOrigin = absOrigin.Value;
            }
            catch
            {
                continue;
            }

            var targetPoints = new[]
            {
                otherOrigin with { Z = otherOrigin.Z + 72f },
                otherOrigin with { Z = otherOrigin.Z + 55f },
                otherOrigin with { Z = otherOrigin.Z + 36f },
                otherOrigin with { Z = otherOrigin.Z + 20f },
            };

            foreach (var targetPoint in targetPoints)
            {
                var delta = targetPoint - eyePos;
                var dist = delta.Length();
                if (dist < 0.1f || dist > 8192f) continue;

                var dir = delta / dist;
                var dot = forward.Dot(dir);
                if (dot < 0.939f) continue;

                TraceResult r;
                try
                {
                    r = Core.Trace.TraceShapeLine(eyePos, targetPoint, hullParams);
                }
                catch
                {
                    continue;
                }

                if (r.DidHit && r.HitPlayer(out var hp) && hp != null && hp.Slot == other.Slot)
                {
                    visible.Add(other.Slot);
                    break;
                }
            }
        }

        return visible;
    }

    private void DetectionTick()
    {
        IEnumerable<IPlayer>? players;
        try
        {
            players = Core.PlayerManager.GetAllPlayers();
        }
        catch
        {
            return;
        }
        if (players == null) return;

        var playersList = players.Where(p => p?.IsValid == true).ToList();

        foreach (var player in playersList)
        {
            if (!player.IsAlive)
            {
                playerTargets.Remove(player.Slot);
                playerDamageTrack.Remove(player.Slot);
                continue;
            }

            CleanDamageTrack(player.Slot);

            var now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerMillisecond;

            if (!playerTargets.TryGetValue(player.Slot, out var state))
            {
                state = new TargetState();
                playerTargets[player.Slot] = state;
            }

            var visible = DetectVisible(playersList, player);

            foreach (var victimSlot in visible)
            {
                if (state.Tracked.TryGetValue(victimSlot, out var tracked))
                {
                    tracked.LastSeenTime = now;
                }
                else
                {
                    state.Tracked[victimSlot] = new TrackedTarget
                    {
                        StartTime = now,
                        LastSeenTime = now,
                        HasOutput = false
                    };
                }
            }

            var expired = new List<int>(state.Tracked.Count);
            foreach (var kv in state.Tracked)
            {
                if (now - kv.Value.LastSeenTime > SIGHT_LOST_TIMEOUT_MS)
                    expired.Add(kv.Key);
            }
            foreach (var victimSlot in expired)
                state.Tracked.Remove(victimSlot);
        }
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerHurt(EventPlayerHurt @event)
    {
        var attacker = @event.AttackerPlayer;
        var victim = @event.UserIdPlayer;
        if (attacker == null || victim == null) return HookResult.Continue;
        if (attacker == victim) return HookResult.Continue;

        var attackerSlot = attacker.Slot;
        var victimSlot = victim.Slot;

        if (!playerDamageTrack.TryGetValue(attackerSlot, out var track))
        {
            track = new Dictionary<int, DamageEntry>();
            playerDamageTrack[attackerSlot] = track;
        }

        var now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerMillisecond;
        if (track.TryGetValue(victimSlot, out var entry))
        {
            entry.LastHitTime = now;
        }
        else
        {
            track[victimSlot] = new DamageEntry { FirstHitTime = now, LastHitTime = now };
        }

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerDeath(EventPlayerDeath @event)
    {
        var attacker = @event.AttackerPlayer;
        var victim = @event.UserIdPlayer;
        if (attacker == null || victim == null) return HookResult.Continue;

        var attackerName = attacker.Controller?.PlayerName ?? "Unknown";
        var victimName = victim.Controller?.PlayerName ?? "Unknown";

        var attackerSlot = attacker.Slot;
        var victimSlot = victim.Slot;

        if (!playerTargets.TryGetValue(attackerSlot, out var state)) return HookResult.Continue;

        double startTime;

        if (state.Tracked.TryGetValue(victimSlot, out var tracked))
        {
            if (tracked.HasOutput) return HookResult.Continue;
            startTime = tracked.StartTime;
            tracked.HasOutput = true;
        }
        else if (playerDamageTrack.TryGetValue(attackerSlot, out var dmgTrack) && dmgTrack.TryGetValue(victimSlot, out var damageEntry))
        {
            startTime = damageEntry.FirstHitTime;
        }
        else
        {
            return HookResult.Continue;
        }

        var now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerMillisecond;
        var elapsed = now - startTime;
        if (elapsed < 0) elapsed = 0;
        if (elapsed > MAX_ELAPSED_MS) elapsed = MAX_ELAPSED_MS;

        Core.Logger.LogInformation($"[KillTime] {attackerName} killed {victimName} in {elapsed:F1}ms");

        var localizer = Core.Translation.GetPlayerLocalizer(attacker);

        string msColor = elapsed < 200 ? "#66FF66" :
                         elapsed < 500 ? "#FFD700" :
                         elapsed < 1000 ? "#FF8C42" : "#FF5555";
        var coloredMs = $"<span color=\"{msColor}\">{elapsed:F1}ms</span>";

        attacker.SendCenterHTML(localizer["kill.output", coloredMs], 2000);

        state.Tracked.Remove(victimSlot);

        if (playerDamageTrack.TryGetValue(attackerSlot, out var cleanTrack))
            cleanTrack.Remove(victimSlot);

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnRoundStart(EventRoundStart @event)
    {
        playerTargets.Clear();
        playerDamageTrack.Clear();
        return HookResult.Continue;
    }

    private sealed class DamageEntry
    {
        public double FirstHitTime;
        public double LastHitTime;
    }

    private sealed class TrackedTarget
    {
        public double StartTime;
        public double LastSeenTime;
        public bool HasOutput;
    }

    private sealed class TargetState
    {
        public Dictionary<int, TrackedTarget> Tracked { get; } = new();
    }
}
