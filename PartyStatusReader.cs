using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace AutoFollow;

/// <summary>Uses the client social roster's explicit disconnected/offline state, not object-table absence.</summary>
internal static class PartyStatusReader
{
    public static unsafe bool? IsOnline(ulong contentId)
    {
        var proxy = InfoProxyPartyMember.Instance();
        if (proxy == null) return null;
        var entry = proxy->GetEntryByContentId(contentId);
        if (entry == null) return null;
        var state = entry->State;
        const InfoProxyCommonList.CharacterData.OnlineStatus unavailable =
            InfoProxyCommonList.CharacterData.OnlineStatus.Disconnected
            | InfoProxyCommonList.CharacterData.OnlineStatus.NotFound
            | InfoProxyCommonList.CharacterData.OnlineStatus.OfflineExd;
        return state != InfoProxyCommonList.CharacterData.OnlineStatus.Offline && (state & unavailable) == 0;
    }
}
