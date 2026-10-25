using System.Numerics;

namespace AutoFollow.Movement;

public sealed class TargetHistory
{
    private readonly Queue<(DateTime Time, Vector3 Position)> _samples = new();
    public Vector3? Last => _samples.Count == 0 ? null : _samples.Last().Position;
    public void Clear() => _samples.Clear();

    // Reject a discontinuity BEFORE storing its remote destination as an entrance.
    public bool Observe(DateTime now, Vector3 position)
    {
        if (!PathRequestGuard.Finite(position)) return false;
        if (_samples.Count > 0)
        {
            var last = _samples.Last();
            var dt = Math.Max(0, (now - last.Time).TotalSeconds);
            if (Vector3.Distance(last.Position, position) > Math.Max(12, dt * 20)) return false;
        }
        _samples.Enqueue((now, position));
        while (_samples.Count > 0 && (now - _samples.Peek().Time).TotalSeconds > 2) _samples.Dequeue();
        return true;
    }

    public bool Fresh(DateTime now) => _samples.Count > 0 && (now - _samples.Last().Time).TotalSeconds <= 2;
    public Vector3? Direction(DateTime now)
    {
        if (!Fresh(now) || _samples.Count < 2) return null;
        var end = _samples.Last();
        var start = _samples.FirstOrDefault(s => (end.Time - s.Time).TotalSeconds >= 0.25);
        if (start.Time == default || (end.Time - start.Time).TotalSeconds > 2) return null;
        var d = end.Position - start.Position;
        if (Math.Abs(d.Y) > 3) return null;
        d.Y = 0;
        return d.LengthSquared() >= 0.25f ? Vector3.Normalize(d) : null;
    }
}
