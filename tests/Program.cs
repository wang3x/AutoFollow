using System.Numerics;
using AutoFollow.Movement;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
}
var requests = new PathRequestGuard();
var first = requests.Next();
var second = requests.Next();
Check(!requests.IsCurrent(first), "Older path cannot replace newer path");
Check(requests.IsCurrent(second), "Newest request accepted");
requests.Next(); // Stop, target switch, loading and disposal all invalidate before stopping.
Check(!requests.IsCurrent(second), "Late completion after stop rejected");
Check(!PathRequestGuard.CompletePath(null, Vector3.One), "Null path rejected");
Check(!PathRequestGuard.CompletePath([], Vector3.One), "Empty path rejected");
Check(!PathRequestGuard.CompletePath([Vector3.Zero], new Vector3(10)), "Partial path rejected");
Check(!PathRequestGuard.CompletePath([new(float.NaN), Vector3.One], Vector3.One), "Invalid waypoint rejected");
Check(PathRequestGuard.CompletePath([Vector3.Zero, Vector3.One], Vector3.One), "Complete path accepted");
var self = new AutoFollow.Models.PartyCandidate(0, 1, true, true, false);
var tank = new AutoFollow.Models.PartyCandidate(1, 2, true, true, true);
var dps = new AutoFollow.Models.PartyCandidate(2, 3, true, true, false);
Check(AutoFollow.Models.PartySelection.Select([self, tank, dps], 1, 0) == 2, "Single tank wins");
Check(AutoFollow.Models.PartySelection.Select([self, tank, dps with { Tank = true }], 1, 2) == 3, "Multiple tanks follow leader");
Check(AutoFollow.Models.PartySelection.Select([self, tank with { Alive = false }, dps], 1, 0) == 3, "Dead tank excluded");
Check(AutoFollow.Models.PartySelection.Select([self, tank with { Online = false }, dps], 1, 0) == 3, "Offline tank excluded");
Check(AutoFollow.Models.PartySelection.Select([self with { Tank = true }, dps], 1, 0) == 3, "Self tank falls back");
Check(AutoFollow.Models.PartySelection.Select([self, tank, dps with { Tank = true }], 1, 0) == 2, "Self leader falls back");
Check(AutoFollow.Models.PartySelection.Select([self], 1, 0) == null, "Never follow self");
Check(AutoFollow.Models.PartySelection.Select([], 1, 0) == null, "Empty party waits");
Check(AutoFollow.Models.PartySelection.Select([self, dps], 1, 0) == 3, "No tanks falls back");
Console.WriteLine($"PASS: {checks} checks");
