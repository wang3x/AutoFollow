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
Console.WriteLine($"PASS: {checks} checks");
