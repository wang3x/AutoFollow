using System.Numerics;
using AutoFollow.Movement;

namespace AutoFollow.Models;

public enum TransitionKind { Walk, Wait, Interact }

/// <summary>Explicitly verified entrance data; no guessed entries are shipped.</summary>
public sealed class TransitionProfile
{
    public string EntryId { get; set; } = "";
    public ushort TerritoryId { get; set; }
    public Vector3 Source { get; set; }
    public float Radius { get; set; } = 3f;
    public TransitionKind Kind { get; set; }
    public Vector3? WalkDestination { get; set; }
    public uint ObjectDataId { get; set; }
    public string? ConfirmationText { get; set; }

    public bool Matches(ushort? territory, Vector3 source)
        => !string.IsNullOrWhiteSpace(EntryId) && TerritoryId != 0 && TerritoryId == territory
            && Enum.IsDefined(Kind) && Radius is > 0 and <= 5 && PathRequestGuard.Finite(Source)
            && Vector3.Distance(Source, source) <= Radius
            && (Kind != TransitionKind.Interact || ObjectDataId != 0)
            && (Kind != TransitionKind.Walk || WalkDestination is Vector3 p
                && PathRequestGuard.Finite(p) && Vector3.Distance(Source, p) is > 0 and <= 5
                && MathF.Abs(Source.Y - p.Y) <= 3);
}
