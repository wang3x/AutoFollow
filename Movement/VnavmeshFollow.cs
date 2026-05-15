using System.Numerics;

namespace AutoFollow.Movement;

/// <summary>vnavmesh 寻路跟随</summary>
public sealed class VnavmeshFollow
{
    private readonly IPC.VnavmeshIPC _vnav;

    public bool IsAvailable => _vnav.IsAvailable;
    public bool IsMoving => _vnav.IsMoving();

    public VnavmeshFollow(IPC.VnavmeshIPC vnav) => _vnav = vnav;

    public void MoveTo(Vector3 from, Vector3 destination)
    {
        if (!IsAvailable) return;
        _vnav.MoveToPositionAsync(from, destination);
    }

    public void Stop()
    {
        if (IsAvailable) _vnav.Stop();
    }
}
