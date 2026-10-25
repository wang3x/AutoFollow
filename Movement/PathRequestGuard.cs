using System.Numerics;

namespace AutoFollow.Movement;

public enum PathStatus { Idle, Pending, Moving, Arrived, Failed, Stopped, Reachable }

/// <summary>Owned by the framework thread. Stop and target changes invalidate queued completions.</summary>
public sealed class PathRequestGuard
{
    private long _version;
    public long Next() => ++_version;
    public bool IsCurrent(long version) => version == _version;
    public static bool CompletePath(IReadOnlyList<Vector3>? path, Vector3 destination)
        => path is { Count: > 0 } && path.All(Finite)
            && Finite(destination) && Vector3.Distance(path[^1], destination) <= 1.5f;
    public static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
