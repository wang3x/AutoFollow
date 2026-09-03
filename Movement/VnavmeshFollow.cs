using System.Numerics;

namespace AutoFollow.Movement;

/// <summary>vnavmesh 寻路跟随</summary>
public sealed class VnavmeshFollow
{
    private readonly IPC.VnavmeshIPC _vnav;

    public bool IsAvailable => _vnav.IsAvailable;
    public bool IsMoving => _vnav.IsMoving();

    public VnavmeshFollow(IPC.VnavmeshIPC vnav) => _vnav = vnav;

    public void MoveTo(Vector3 from, Vector3 destination, Vector3? fallbackDestination = null)
    {
        if (!IsAvailable) return;
        _vnav.MoveToPositionAsync(from, destination, fallbackDestination);
    }

    public void Stop()
    {
        if (IsAvailable) _vnav.Stop();
    }

    /// <summary>使所有挂起的寻路结果失效（防止旧 MoveTo 结果晚到覆盖后续移动）</summary>
    public void InvalidatePaths() { if (IsAvailable) _vnav.InvalidatePaths(); }
}
