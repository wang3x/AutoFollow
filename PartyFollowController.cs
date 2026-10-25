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
    private bool _suspended;
    public bool Enabled { get; private set; }

    public PartyFollowController(IPartyList party, IObjectTable objects, IFramework framework,
        IDtrBar dtr, FollowEngine engine)
    {
        _party = party; _objects = objects; _framework = framework; _engine = engine;
        _entry = dtr.Get("AutoFollow", "?? OFF");
        _entry.OnClick = _ => Toggle();
        _engine.ManualTargetSelected += Disable;
        _engine.OnStateChanged += OnStateChanged;
        _framework.Update += Update;
    }

    public void Disable() { Enabled = false; _suspended = false; Refresh(); }
    private void Toggle()
    {
        if (Enabled) { Disable(); _engine.Pause("??????"); }
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
        _entry.Text = Enabled ? $"?? ON {(_suspended ? "??" : _engine.TargetName ?? "????")}" : "?? OFF";
        _entry.Tooltip = $"??????????\n{_engine.PauseReason ?? _engine.State.ToString()}\n????????????? OFF ? ON ????";
    }

    private void Update(IFramework _)
    {
        if (DateTime.UtcNow < _nextScan) return;
        _nextScan = DateTime.UtcNow.AddMilliseconds(500);
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
        if (current != null && current.CurrentHP > 0 && current.GameObject == null) return;
        var members = new List<PartyCandidate>();
        for (int i = 0; i < _party.Length; i++)
        {
            var m = _party[i];
            if (m == null) continue;
            members.Add(new(i, m.ContentId, m.CurrentHP > 0 && m.GameObject?.IsDead != true,
                m.GameObject != null, m.ClassJob.IsValid && m.ClassJob.Value.Role == 1));
        }
        var self = _party.FirstOrDefault(m => m.EntityId == _objects.LocalPlayer.EntityId)?.ContentId ?? 0;
        if (self == 0) { _engine.WaitForParty("??????????"); return; }
        var selected = PartySelection.Select(members, self, _party.PartyLeaderIndex);
        if (selected == null)
        {
            _engine.WaitForParty("??????????");
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
