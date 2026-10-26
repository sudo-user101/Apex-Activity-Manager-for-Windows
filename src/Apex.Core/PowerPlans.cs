using System.Text.RegularExpressions;

namespace Apex.Core;

public sealed record PowerPlan(Guid Id, string Name, bool Active);

public static class PowerPlanList
{
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    /// `powercfg /list` output → plans. The GUID is what matters; names are
    /// in the PC's language. Pure: tested.
    public static List<PowerPlan> Parse(string output)
    {
        var list = new List<PowerPlan>();
        foreach (Match m in Regex.Matches(output, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+\(([^)]*)\)\s*(\*)?"))
            list.Add(new PowerPlan(Guid.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim(), m.Groups[3].Success));
        return list;
    }

    /// The fastest plan this PC has (Ultimate, then High performance); null if neither exists.
    public static PowerPlan? Fastest(IEnumerable<PowerPlan> plans) =>
        plans.FirstOrDefault(p => p.Id == UltimatePerformance)
        ?? plans.FirstOrDefault(p => p.Id == HighPerformance)
        ?? plans.FirstOrDefault(p => p.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("High performance", StringComparison.OrdinalIgnoreCase));
}
