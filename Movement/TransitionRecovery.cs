using System.Numerics;
using AutoFollow.Models;

namespace AutoFollow.Movement;

public enum RecoveryPhase { Idle, Approaching, Advancing, Waiting, Paused }
public enum RecoveryAction { None, Navigate, Direct, Stop, Interact, Probe, Resume }
public readonly record struct RecoveryStep(RecoveryAction Action, Vector3 Destination = default);

/// <summary>One bounded attempt per loss episode. A failed attempt can only resume via a complete-path probe.</summary>
public sealed class TransitionRecovery
{
    public RecoveryPhase Phase { get; private set; }
    public TransitionProfile? Profile { get; private set; }
    public string Reason { get; private set; } = "";
    private Vector3 _source;
    private Vector3? _direction;
    private DateTime _phaseStart;
    private DateTime _progressAt;
    private Vector3 _progressPosition;
    private Vector3 _previousPosition;
    private float _travelled;
    private Vector3 _advanceDestination;
    private bool _probing;
    private DateTime _nextProbe;
    private Vector3 _probeDestination;
    public bool Active => Phase != RecoveryPhase.Idle;

    public void Clear()
    {
        Phase = RecoveryPhase.Idle; Profile = null; _probing = false;
    }

    public RecoveryStep Begin(DateTime now, Vector3? player, Vector3? source, Vector3? direction,
        TransitionProfile? profile, bool allowAttempt)
    {
        Clear(); Profile = profile; _direction = direction; _nextProbe = now;
        if (!allowAttempt || player == null || source == null || !PathRequestGuard.Finite(source.Value)
            || Vector3.Distance(player.Value, source.Value) > 30)
            return Fail("目标丢失，无可靠入口，已停止");
        _source = profile?.Source ?? source.Value;
        if (Vector3.Distance(player.Value, _source) > 30) return Fail("入口超出尝试范围");
        Phase = RecoveryPhase.Approaching; Reason = "前往最后可靠入口";
        _phaseStart = _progressAt = now; _progressPosition = player.Value;
        return new(RecoveryAction.Navigate, _source);
    }

    public RecoveryStep Hold(DateTime now)
    {
        _probing = false; Profile = null;
        return Wait(now, "加载或过场中，停止移动");
    }

    public RecoveryStep Fail(string reason)
    {
        Phase = RecoveryPhase.Paused; Reason = reason; _probing = false;
        return new(RecoveryAction.Stop);
    }

    private RecoveryStep Wait(DateTime now, string reason)
    {
        Phase = RecoveryPhase.Waiting; Reason = reason; _phaseStart = now;
        return new(RecoveryAction.Stop);
    }

    public RecoveryStep Tick(DateTime now, Vector3 player, PathStatus status, Vector3? target)
    {
        if (!Active) return default;
        // A target that returned to the source side no longer needs a speculative push.
        if (Phase is RecoveryPhase.Approaching or RecoveryPhase.Advancing
            && target is Vector3 t && Vector3.Distance(t, _source) <= 5)
            return Wait(now, "目标重新出现，验证路径");

        if (Phase is RecoveryPhase.Waiting or RecoveryPhase.Paused)
        {
            if (_probing)
            {
                if (target == null) { _probing = false; return new(RecoveryAction.Stop); }
                if (Vector3.Distance(target.Value, _probeDestination) > 3)
                { _probing = false; _nextProbe = now; return new(RecoveryAction.Stop); }
                if (status == PathStatus.Reachable)
                {
                    Clear(); return new(RecoveryAction.Resume);
                }
                if (status != PathStatus.Pending) { _probing = false; _nextProbe = now.AddSeconds(1); }
            }
            else if (target is Vector3 destination && now >= _nextProbe)
            {
                _probing = true; _probeDestination = destination; return new(RecoveryAction.Probe, destination);
            }
            if (Phase == RecoveryPhase.Waiting && (now - _phaseStart).TotalSeconds >= 10)
                return Fail("传送等待超时，保持停止并等待有效路径");
            return default;
        }

        if (Phase == RecoveryPhase.Approaching)
        {
            if ((now - _phaseStart).TotalSeconds >= 10) return Fail("接近入口超时");
            if (status is PathStatus.Failed or PathStatus.Stopped) return Fail("无法到达入口，停止尝试");
            if (Vector3.Distance(player, _source) <= (Profile == null ? 1.5f : 0.5f))
            {
                if (Profile?.Kind == TransitionKind.Interact)
                {
                    Wait(now, "等待已确认的交互传送");
                    return new(RecoveryAction.Interact);
                }
                if (Profile?.Kind == TransitionKind.Wait) return Wait(now, "在入口等待传送");
                Vector3? destination = Profile?.WalkDestination;
                if (destination == null && _direction is Vector3 direction) destination = player + direction * 5;
                if (destination == null) return Wait(now, "无可靠方向，原地等待传送");
                if (Vector3.Distance(player, destination.Value) > 5)
                    destination = player + Vector3.Normalize(destination.Value - player) * 5;
                _advanceDestination = destination.Value;
                Phase = RecoveryPhase.Advancing; Reason = "限时直走尝试传送";
                _phaseStart = _progressAt = now;
                _progressPosition = _previousPosition = player; _travelled = 0;
                return new(RecoveryAction.Direct, destination.Value);
            }
        }
        else if (Phase == RecoveryPhase.Advancing)
        {
            var delta = Vector3.Distance(player, _previousPosition);
            _previousPosition = player; _travelled += delta;
            if (delta > 12) return Wait(now, "位置已变化，验证传送结果");
            if ((now - _phaseStart).TotalSeconds >= 2 || _travelled >= 5
                || Vector3.Distance(player, _advanceDestination) <= 0.5f)
                return Wait(now, "直走尝试结束，等待传送");
            if (status is PathStatus.Failed or PathStatus.Stopped) return Fail("直走被中断，停止尝试");
        }
        if (status == PathStatus.Moving)
        {
            if (Vector3.Distance(player, _progressPosition) >= 0.2f)
            { _progressPosition = player; _progressAt = now; }
            else if ((now - _progressAt).TotalSeconds >= 1) return Fail("移动受阻，停止尝试");
        }
        else _progressAt = now; // A pending path is not a blocked character.
        return default;
    }
}
