using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;
using AutoFollow.Models;

namespace AutoFollow;

/// <summary>Runtime-only intent: loading/reloading never arms automatic movement.</summary>
public sealed class PartyFollowController : IDisposable
{
    private readonly IPartyList _party;
    private readonly IObjectTable _objects;
    private readonly IFramework _framework;
    private readonly FollowEngine _engine;
    private readonly IDtrBarEntry _entry;
    private DateTime _nextScan;
    private readonly Func<DateTime> _now;
    private bool _suspended;
    public bool Enabled { get; private set; }

    public PartyFollowController(IPartyList party, IObjectTable objects, IFramework framework,
        IDtrBar dtr, FollowEngine engine, Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        _party = party; _objects = objects; _framework = framework; _engine = engine;
        _entry = dtr.Get("AutoFollow", "跟随 OFF");
        _entry.OnClick = _ => Toggle();
        _engine.ManualTargetSelected += Disable;
        _engine.OnStateChanged += OnStateChanged;
        _framework.Update += Update;
    }

    public void Disable() { Enabled = false; _suspended = false; Refresh(); }
    private void Toggle()
    {
        if (Enabled) { Disable(); _engine.Pause("自动跟随关闭"); }
        else
        {
            Enabled = true; _suspended = false; _nextScan = DateTime.MinValue;
            _engine.ResetToIdle();
        }
        Refresh();
    }

    private void OnStateChanged(FollowState oldState, FollowState state)
    {
        if (state is FollowState.Paused or FollowState.EmergencyStopped) _suspended = true;
        // Only an explicit user resume can leave a manual pause.
        if (oldState is FollowState.Paused or FollowState.EmergencyStopped && state == FollowState.Following)
            _suspended = false;
        Refresh();
    }

    private void Refresh()
    {
        _entry.Text = Enabled ? $"跟随 ON {(_suspended ? "暂停" : _engine.IsRecovering ? "等待" : _engine.TargetName ?? "等待小队")}" : "跟随 OFF";
        _entry.Tooltip = $"点击切换自动小队跟随\n{_engine.PauseReason ?? _engine.State.ToString()}\n暂停后可用原有恢复按钮，或 OFF → ON 重新开启";
    }

    private void Update(IFramework _)
    {
        if (_now() < _nextScan) return;
        _nextScan = _now().AddMilliseconds(500);
        Refresh();
        if (!Enabled || _suspended || _objects.LocalPlayer == null) return;
        if (_party.Length < 2)
        {
            if (_engine.TargetContentId != null) _engine.ResetToIdle();
            return;
        }
        var locked = _engine.TargetContentId;
        var current = _party.FirstOrDefault(m => m.ContentId == locked);
        // Object-table absence is not disconnection. Let the engine recover this identity first.
        if (current != null && current.CurrentHP > 0 && PartyStatusReader.IsOnline(current.ContentId) != false
            && current.GameObject == null) return;
        var members = new List<PartyCandidate>();
        for (int i = 0; i < _party.Length; i++)
        {
            var m = _party[i];
            if (m == null) continue;
            var online = PartyStatusReader.IsOnline(m.ContentId);
            members.Add(new(i, m.ContentId, m.CurrentHP > 0 && m.GameObject?.IsDead != true,
                online == true || (online == null && m.GameObject != null), m.ClassJob.IsValid && m.ClassJob.Value.Role == 1));
        }
        var self = _party.FirstOrDefault(m => m.EntityId == _objects.LocalPlayer.EntityId)?.ContentId ?? 0;
        if (self == 0) { _engine.WaitForParty("无法确认自身小队身份"); return; }
        var selected = PartySelection.Select(members, self, _party.PartyLeaderIndex);
        if (selected == null)
        {
            _engine.WaitForParty("没有存活且在线的队友");
            return;
        }
        if (selected == locked) return;
        var target = _party.First(m => m.ContentId == selected);
        _engine.SetTarget(target.Name.TextValue, target.ContentId, automatic: true);
    }

    public void Dispose()
    {
        _framework.Update -= Update;
        _engine.ManualTargetSelected -= Disable;
        _engine.OnStateChanged -= OnStateChanged;
        _entry.Remove();
    }
}
