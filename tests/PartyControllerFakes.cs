// Service-boundary fakes for the actual PartyFollowController (no game client required).
using AutoFollow.Models;
namespace Dalamud.Game.Gui.Dtr
{
    public interface IDtrBarEntry
    {
        string Text { get; set; }
        string Tooltip { get; set; }
        Action<object> OnClick { get; set; }
        void Remove();
    }
}
namespace Dalamud.Plugin.Services
{
    public interface IFramework { event Action<IFramework> Update; }
    public interface IDtrBar { Dalamud.Game.Gui.Dtr.IDtrBarEntry Get(string name, string text); }
    public interface IObjectTable { FakePlayer? LocalPlayer { get; } }
    public interface IPartyList : IEnumerable<FakeMember>
    {
        int Length { get; }
        uint PartyLeaderIndex { get; }
        FakeMember? this[int index] { get; }
    }
    public sealed class FakePlayer { public uint EntityId { get; set; } public bool IsDead { get; set; } }
    public sealed class FakeName { public string TextValue { get; set; } = "Player"; }
    public sealed class FakeJobValue { public byte Role { get; set; } }
    public sealed class FakeJob { public bool IsValid => true; public FakeJobValue Value { get; } = new(); }
    public sealed class FakeMember
    {
        public ulong ContentId { get; set; }
        public uint EntityId { get; set; }
        public uint CurrentHP { get; set; } = 100;
        public FakePlayer? GameObject { get; set; } = new();
        public FakeJob ClassJob { get; } = new();
        public FakeName Name { get; } = new();
    }
}
namespace AutoFollow
{
    public static class PartyStatusReader
    {
        public static readonly Dictionary<ulong, bool?> States = new();
        public static bool? IsOnline(ulong id) => States.GetValueOrDefault(id);
    }
    public sealed class FollowEngine
    {
        public event Action? ManualTargetSelected;
        public event Action<FollowState, FollowState>? OnStateChanged;
        public FollowState State { get; private set; }
        public ulong? TargetContentId { get; private set; }
        public string? TargetName { get; private set; }
        public string? PauseReason { get; private set; }
        public bool IsRecovering { get; set; }
        public int Selections { get; private set; }
        public int Resets { get; private set; }
        public void ChangeState(FollowState state)
        { var old = State; State = state; OnStateChanged?.Invoke(old, state); }
        public void ResetToIdle()
        { Resets++; TargetContentId = null; TargetName = null; IsRecovering = false; ChangeState(FollowState.Idle); }
        public void Pause(string reason) { PauseReason = reason; ChangeState(FollowState.Paused); }
        public void WaitForParty(string reason) { ResetToIdle(); PauseReason = reason; }
        public void SetTarget(string name, ulong? contentId = null, bool automatic = false)
        {
            if (!automatic) ManualTargetSelected?.Invoke();
            Selections++; TargetContentId = contentId; TargetName = name; ChangeState(FollowState.Following);
        }
    }
}
namespace AutoFollow.Tests
{
    using Dalamud.Plugin.Services;
    using Dalamud.Game.Gui.Dtr;
    public sealed class FrameworkFake : IFramework
    {
        public event Action<IFramework>? Update;
        public void Tick() => Update?.Invoke(this);
    }
    public sealed class ObjectsFake : IObjectTable { public FakePlayer? LocalPlayer { get; set; } = new() { EntityId = 1 }; }
    public sealed class PartyFake : List<FakeMember>, IPartyList
    {
        public int Length => Count;
        public uint PartyLeaderIndex { get; set; }
    }
    public sealed class DtrFake : IDtrBar, IDtrBarEntry
    {
        public string Text { get; set; } = "";
        public string Tooltip { get; set; } = "";
        public Action<object> OnClick { get; set; } = _ => { };
        public bool Removed { get; private set; }
        public IDtrBarEntry Get(string name, string text) { Text = text; return this; }
        public void Remove() => Removed = true;
        public void Click() => OnClick(new object());
    }
}
