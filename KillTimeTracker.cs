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
    private const float TRACE_DISTANCE = 8192.0f;
    private const double MAX_ELAPSED_MS = 9999.0;
    private const double SIGHT_LOST_TIMEOUT_MS = 3000.0;

    private readonly Dictionary<int, TargetState> playerTargets = new();
    private readonly Dictionary<int, Dictionary<int, DamageEntry>> playerDamageTrack = new();

    public KillTimeTracker(ISwiftlyCore core) : base(core)
    {
    }

    public override void Load(bool hotReload)
    {
        Core.Event.OnTick += DetectionTick;
        Core.Event.OnClientDisconnected += OnClientDisconnected;

        Core.Logger.LogInformation("[KillTimeTracker] Plugin loaded successfully.");
    }

    public override void Unload()
    {
        Core.Event.OnTick -= DetectionTick;
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
        var expired = new List<int>();
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

    private void DetectionTick()
    {
        var players = Core.PlayerManager.GetAllPlayers();
        if (players == null) return;

        foreach (var player in players)
        {
            if (player?.IsValid != true)
                continue;

            if (!player.IsAlive)
            {
                if (playerTargets.TryGetValue(player.Slot, out var deadState))
                {
                    if (deadState.CurrentTargetSlot >= 0 && playerTargets.TryGetValue(deadState.CurrentTargetSlot, out var oldTargetState))
                        oldTargetState.ResetByObserver();

                    playerTargets.Remove(player.Slot);
                }

                playerDamageTrack.Remove(player.Slot);
                continue;
            }

            CleanDamageTrack(player.Slot);

            var target = DetectTarget(player);

            var slot = player.Slot;
            if (!playerTargets.TryGetValue(slot, out var state))
            {
                state = new TargetState();
                playerTargets[slot] = state;
            }

            if (target != null)
            {
                var now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerMillisecond;
                state.LastSeenTime = now;

                var targetSlot = target.Slot;

                if (state.CurrentTargetSlot != targetSlot)
                {
                    if (state.CurrentTargetSlot >= 0)
                    {
                        if (playerTargets.TryGetValue(state.CurrentTargetSlot, out var oldTargetState))
                            oldTargetState.ResetByObserver();
                    }

                    state.CurrentTargetSlot = targetSlot;
                    state.StartTime = now;
                    state.HasOutput = false;
                }
            }
            else
            {
                if (state.CurrentTargetSlot >= 0)
                {
                    var now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerMillisecond;
                    if (now - state.LastSeenTime > SIGHT_LOST_TIMEOUT_MS)
                    {
                        if (playerTargets.TryGetValue(state.CurrentTargetSlot, out var oldTargetState))
                            oldTargetState.ResetByObserver();

                        state.CurrentTargetSlot = -1;
                        state.StartTime = 0;
                        state.HasOutput = false;
                        state.LastSeenTime = 0;
                    }
                }
            }
        }
    }

    private IPlayer? DetectTarget(IPlayer player)
    {
        var pawn = player.PlayerPawn;
        if (pawn == null) return null;

        var eyePos = pawn.EyePosition;
        if (eyePos == null) return null;

        var eyeAngles = pawn.EyeAngles;

        var traceParams = TraceParams.Builder()
            .InteractWith(MaskTrace.Solid | MaskTrace.Player | MaskTrace.Hitbox | MaskTrace.WorldGeometry)
            .InteractExclude(MaskTrace.Trigger)
            .IgnoreEntity(pawn)
            .Build();

        var result = Core.Trace.TraceShapeAngle(eyePos.Value, eyeAngles, TRACE_DISTANCE, traceParams);

        if (!result.DidHit) return null;
        if (!result.HitPlayer(out var targetPlayer)) return null;
        if (targetPlayer == null) return null;
        if (!targetPlayer.IsValid || !targetPlayer.IsAlive) return null;

        return targetPlayer;
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
        if (!playerTargets.TryGetValue(attackerSlot, out var state))
        {
            state = new TargetState();
            playerTargets[attackerSlot] = state;
        }

        if (state.CurrentTargetSlot != victim.Slot)
        {
            if (state.CurrentTargetSlot != -1) return HookResult.Continue;

            if (playerDamageTrack.TryGetValue(attackerSlot, out var track) && track.TryGetValue(victim.Slot, out var damageEntry))
            {
                state.StartTime = damageEntry.FirstHitTime;
            }
            else
            {
                state.StartTime = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerMillisecond;
            }

            state.CurrentTargetSlot = victim.Slot;
        }

        if (state.HasOutput) return HookResult.Continue;

        var elapsed = (DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerMillisecond) - state.StartTime;
        if (elapsed < 0) elapsed = 0;
        if (elapsed > MAX_ELAPSED_MS) elapsed = MAX_ELAPSED_MS;

        Core.Logger.LogInformation($"[KillTime] {attackerName} killed {victimName} in {elapsed:F1}ms");

        var localizer = Core.Translation.GetPlayerLocalizer(attacker);

        string msColor = elapsed < 200 ? "#66FF66" :
                         elapsed < 500 ? "#FFD700" :
                         elapsed < 1000 ? "#FF8C42" : "#FF5555";
        var coloredMs = $"<span color=\"{msColor}\">{elapsed:F1}ms</span>";

        attacker.SendCenterHTML(localizer["kill.output", coloredMs], 2000);

        state.CurrentTargetSlot = -1;
        state.StartTime = 0;
        state.HasOutput = false;
        state.LastSeenTime = 0;

        if (playerDamageTrack.TryGetValue(attackerSlot, out var dmgTrack))
            dmgTrack.Remove(victim.Slot);

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

    private sealed class TargetState
    {
        public int CurrentTargetSlot = -1;
        public double StartTime;
        public bool HasOutput;
        public double LastSeenTime;

        public void ResetByObserver()
        {
            CurrentTargetSlot = -1;
            HasOutput = false;
        }
    }
}
