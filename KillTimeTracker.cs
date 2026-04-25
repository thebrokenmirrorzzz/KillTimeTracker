using System.Collections.Concurrent;
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
    private const float FOV_COS = 0.939f;

    private readonly ConcurrentDictionary<int, TargetState> playerTargets = new();
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, DamageEntry>> playerDamageTrack = new();
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

        playerTargets.TryRemove(player.Slot, out _);
        playerDamageTrack.TryRemove(player.Slot, out _);
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
            track.TryRemove(victimSlot, out _);

        if (track.Count == 0)
            playerDamageTrack.TryRemove(attackerSlot, out _);
    }

    private void DetectionTick()
    {
        if (detectionCts?.IsCancellationRequested == true) return;

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

        var now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerMillisecond;
        var workItems = new List<TraceWorkItem>();

        foreach (var player in players)
        {
            if (player?.IsValid != true) continue;

            if (!player.IsAlive)
            {
                playerTargets.TryRemove(player.Slot, out _);
                playerDamageTrack.TryRemove(player.Slot, out _);
                continue;
            }

            CleanDamageTrack(player.Slot);

            var pawn = player.PlayerPawn;
            if (pawn?.IsValid != true) continue;

            Vector? eyePosNullable;
            QAngle eyeAngles;
            try
            {
                eyePosNullable = pawn.EyePosition;
                if (eyePosNullable == null) continue;
                eyeAngles = pawn.EyeAngles;
            }
            catch
            {
                continue;
            }

            var eyePos = eyePosNullable.Value;
            eyeAngles.ToDirectionVectors(out var forward, out var _, out var _);

            if (!playerTargets.TryGetValue(player.Slot, out var state))
            {
                state = new TargetState();
                playerTargets[player.Slot] = state;
            }

            var lineParams = TraceParams.Builder()
                .InteractWith(MaskTrace.Player)
                .InteractExclude(MaskTrace.Trigger)
                .IgnoreEntity(pawn)
                .Build();

            foreach (var other in players)
            {
                if (other?.IsValid != true || !other.IsAlive) continue;
                if (other.Slot == player.Slot) continue;

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

                var delta = new Vector(otherOrigin.X, otherOrigin.Y, otherOrigin.Z + 55f) - eyePos;
                var dir = delta.Normalized();
                var dot = forward.Dot(dir);
                if (dot < FOV_COS) continue;

                workItems.Add(new TraceWorkItem(
                    player.Slot,
                    other.Slot,
                    eyePos,
                    otherOrigin,
                    lineParams
                ));
            }

            var expired = new List<int>(state.Tracked.Count);
            foreach (var kv in state.Tracked)
            {
                if (now - kv.Value.LastSeenTime > SIGHT_LOST_TIMEOUT_MS)
                    expired.Add(kv.Key);
            }
            foreach (var victimSlot in expired)
                state.Tracked.TryRemove(victimSlot, out _);
        }

        if (workItems.Count == 0) return;

        var capturedItems = workItems.ToArray();
        var capturedCore = Core;
        var capturedTargets = playerTargets;
        var capturedNow = now;

        Task.Run(() =>
        {
            var results = new List<(int ViewerSlot, int TargetSlot, bool Visible)>(capturedItems.Length);

            foreach (var item in capturedItems)
            {
                var visible = TraceToPlayerStatic(
                    item.EyePos,
                    item.TargetOrigin,
                    item.TargetSlot,
                    item.ForwardParams,
                    capturedCore
                );
                results.Add((item.ViewerSlot, item.TargetSlot, visible));
            }

            capturedCore.Scheduler.NextTick(() =>
            {
                foreach (var (viewerSlot, targetSlot, visible) in results)
                {
                    if (!visible) continue;

                    if (capturedTargets.TryGetValue(viewerSlot, out var st))
                    {
                        st.Tracked.AddOrUpdate(
                            targetSlot,
                            _ => new TrackedTarget
                            {
                                StartTime = capturedNow,
                                LastSeenTime = capturedNow,
                                HasOutput = false
                            },
                            (_, tracked) =>
                            {
                                tracked.LastSeenTime = capturedNow;
                                return tracked;
                            });
                    }
                }
            });
        });
    }

    private static bool TraceToPlayerStatic(
        Vector from,
        Vector targetOrigin,
        int targetSlot,
        TraceParams forwardParams,
        ISwiftlyCore core)
    {
        var crossPoints = new[]
        {
            new Vector(0, 0, 72f),     // head
            new Vector(0, 0, 55f),     // chest (center)
            new Vector(0, 0, 36f),     // waist
            new Vector(0, 0, 20f),     // feet
            new Vector(-32f, 0, 55f),  // left shoulder
            new Vector(32f, 0, 55f),   // right shoulder
        };

        foreach (var offset in crossPoints)
        {
            var point = targetOrigin + offset;

            var delta = point - from;
            var dist = delta.Length();
            if (dist < 0.1f || dist > 8192f) continue;

            var angle = delta.ToQAngles();
            TraceResult r;
            try
            {
                r = core.Trace.TraceShapeAngle(from, angle, dist, forwardParams);
            }
            catch
            {
                continue;
            }

            if (r.HitPlayer(out var hp) && hp != null && hp.Slot == targetSlot)
                return true;
        }

        return false;
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

        var track = playerDamageTrack.GetOrAdd(attackerSlot, _ => new ConcurrentDictionary<int, DamageEntry>());

        var now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerMillisecond;
        track.AddOrUpdate(
            victimSlot,
            _ => new DamageEntry { FirstHitTime = now, LastHitTime = now },
            (_, entry) =>
            {
                entry.LastHitTime = now;
                return entry;
            });

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

        state.Tracked.TryRemove(victimSlot, out _);

        if (playerDamageTrack.TryGetValue(attackerSlot, out var cleanTrack))
            cleanTrack.TryRemove(victimSlot, out _);

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnRoundStart(EventRoundStart @event)
    {
        playerTargets.Clear();
        playerDamageTrack.Clear();
        return HookResult.Continue;
    }

    private readonly struct TraceWorkItem
    {
        public readonly int ViewerSlot;
        public readonly int TargetSlot;
        public readonly Vector EyePos;
        public readonly Vector TargetOrigin;
        public readonly TraceParams ForwardParams;

        public TraceWorkItem(
            int viewerSlot,
            int targetSlot,
            Vector eyePos,
            Vector targetOrigin,
            TraceParams forwardParams)
        {
            ViewerSlot = viewerSlot;
            TargetSlot = targetSlot;
            EyePos = eyePos;
            TargetOrigin = targetOrigin;
            ForwardParams = forwardParams;
        }
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
        public ConcurrentDictionary<int, TrackedTarget> Tracked { get; } = new();
    }
}
