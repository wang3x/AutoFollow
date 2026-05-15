namespace AutoFollow.Models;

/// <summary>距离分区</summary>
public enum DistanceZone
{
    /// <summary>战斗区 — 距离 &lt; CombatRange</summary>
    Combat,

    /// <summary>跟随区 — 正常跟随范围内</summary>
    Following,

    /// <summary>追赶区 — 超过 SprintThreshold</summary>
    CatchingUp,

    /// <summary>远距区 — 超过 MaxFollowDistance</summary>
    FarAway,

    /// <summary>掉队区 — 超过 TeleportThreshold</summary>
    Teleport,
}
