using System.Numerics;

namespace AutoFollow.Utilities;

/// <summary>全局颜色常量，统一定义所有 UI 颜色</summary>
public static class FollowColors
{
    // ── 状态指示色 ──
    public static readonly Vector4 Idle       = new(0.45f, 0.45f, 0.45f, 1f); // 灰
    public static readonly Vector4 Following  = new(0.20f, 0.80f, 0.30f, 1f); // 亮绿
    public static readonly Vector4 Paused     = new(1.00f, 0.70f, 0.10f, 1f); // 琥珀
    public static readonly Vector4 Combat     = new(1.00f, 0.20f, 0.20f, 1f); // 红
    public static readonly Vector4 TargetLost = new(0.80f, 0.40f, 0.00f, 1f); // 橙
    public static readonly Vector4 Emergency  = new(1.00f, 0.00f, 0.00f, 1f); // 亮红

    // ── 文字色 ──
    public static readonly Vector4 TextPrimary   = new(1.00f, 1.00f, 1.00f, 0.95f);
    public static readonly Vector4 TextSecondary = new(0.70f, 0.70f, 0.70f, 0.80f);
    public static readonly Vector4 TextAccent    = new(1.00f, 0.60f, 0.20f, 1f); // 引擎状态提示
    public static readonly Vector4 TextMuted     = new(0.50f, 0.50f, 0.50f, 0.70f);

    // ── 距离分区色 ──
    public static readonly Vector4 DistClose = new(0.20f, 0.80f, 0.30f, 1f); // ≤5y  绿
    public static readonly Vector4 DistMid   = new(1.00f, 0.70f, 0.10f, 1f); // ≤15y 琥珀
    public static readonly Vector4 DistFar   = new(1.00f, 0.50f, 0.00f, 1f); // ≤30y 橙
    public static readonly Vector4 DistGone  = new(1.00f, 0.20f, 0.20f, 1f); // >30y  红

    // ── 背景 ──
    public static readonly Vector4 BgDark = new(0.06f, 0.06f, 0.08f, 0.92f);

    public static Vector4 GetDistColor(float yalms) => yalms switch
    {
        <= 5f  => DistClose,
        <= 15f => DistMid,
        <= 30f => DistFar,
        _      => DistGone,
    };

    public static Vector4 ForState(Models.FollowState state) => state switch
    {
        Models.FollowState.Idle             => Idle,
        Models.FollowState.Following        => Following,
        Models.FollowState.Combat           => Combat,
        Models.FollowState.Paused           => Paused,
        Models.FollowState.TargetLost       => TargetLost,
        Models.FollowState.EmergencyStopped => Emergency,
        Models.FollowState.CatchingUp       => DistFar,
        _                                   => TextSecondary,
    };
}
