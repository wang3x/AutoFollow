namespace AutoFollow.Models;

public readonly record struct PartyCandidate(int Slot, ulong ContentId, bool Alive, bool Online, bool Tank);

public static class PartySelection
{
    public static ulong? Select(IReadOnlyList<PartyCandidate> members, ulong self, uint leaderSlot)
    {
        var valid = members.Where(m => m.ContentId != 0 && m.Alive && m.Online).OrderBy(m => m.Slot).ToArray();
        var tanks = valid.Where(m => m.Tank).ToArray();
        PartyCandidate? preferred = tanks.Length == 1 ? tanks[0]
            : tanks.Length > 1 ? valid.Cast<PartyCandidate?>().FirstOrDefault(m => m!.Value.Slot == leaderSlot) : null;
        if (preferred is PartyCandidate p && p.ContentId != self) return p.ContentId;
        return valid.Where(m => m.ContentId != self).Select(m => (ulong?)m.ContentId).FirstOrDefault();
    }
}
