namespace AutoFollow.Utilities;

/// <summary>格式化工具</summary>
public static class FormatHelper
{
    /// <summary>格式化距离显示，保证固定宽度避免视觉抖动</summary>
    public static string Dist(float yalms)
    {
        if (yalms > 150f) return " --- ";
        if (yalms > 100f) return $"{yalms,5:F0}";
        if (yalms > 10f)  return $"{yalms,5:F1}";
        return $"{yalms,5:F1}";
    }

    /// <summary>紧凑距离（无对齐填充）</summary>
    public static string DistCompact(float yalms)
    {
        if (yalms > 150f) return "---";
        if (yalms > 100f) return $"{yalms:F0}";
        return $"{yalms:F1}";
    }
}
