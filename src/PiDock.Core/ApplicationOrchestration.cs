namespace PiDock.Core;

public sealed record ApplicationDependency(string AppId, string Condition = "ready");
public sealed record ApplicationDependencyStatus(string AppId, string Condition, bool Satisfied, string Reason);
public sealed record ApplicationDependencyNode(string AppId, string ContainerId, ApplicationDependency[] Dependencies);
public sealed record ApplicationOrchestrationSupport(int SchemaVersion, bool Enabled, int MaximumApps, int MaximumDependencies, int ShutdownDeadlineSeconds, string[] Limitations);
public sealed record ApplicationShutdownPlan(Guid OperationId, string Reason, string Phase, string[] Order, int NextIndex, DateTimeOffset RequestedAt);
public sealed record ApplicationOrchestrationStatus(bool Paused, string? Reason, string? Phase, int Stopped, int Total);

public static class ApplicationDependencyGraph
{
    public static void Validate(ApplicationDependency[] dependencies, string? owner = null)
    {
        if (dependencies.Length > 8 || dependencies.Any(d => d is null || !IdentityRules.IsId(d.AppId) || d.AppId.Length > 40 || d.AppId == owner || d.Condition is not ("ready" or "running")) || dependencies.Select(d=>d.AppId).Distinct(StringComparer.Ordinal).Count()!=dependencies.Length)
            throw new ArgumentException("Use at most eight distinct app dependencies with ready/running conditions and no self dependency.");
    }
    public static ApplicationDependencyNode[] Order(IEnumerable<ApplicationDependencyNode> source)
    {
        var nodes=source.ToArray();
        if(nodes.Length>16 || nodes.Select(n=>n.AppId).Distinct(StringComparer.Ordinal).Count()!=nodes.Length)throw new ArgumentException("Application identities must be unique within the 16-app limit.");
        var map=nodes.ToDictionary(n=>n.AppId,StringComparer.Ordinal);var marks=new Dictionary<string,int>(StringComparer.Ordinal);var ordered=new List<ApplicationDependencyNode>();
        foreach(var node in nodes.OrderBy(n=>n.AppId,StringComparer.Ordinal)) Visit(node);
        return ordered.ToArray();
        void Visit(ApplicationDependencyNode node)
        {
            Validate(node.Dependencies,node.AppId);
            if(marks.TryGetValue(node.AppId,out var mark)){if(mark==1)throw new ArgumentException("Application dependency cycle detected.");return;}
            marks[node.AppId]=1;
            foreach(var dependency in node.Dependencies.OrderBy(d=>d.AppId,StringComparer.Ordinal))if(map.TryGetValue(dependency.AppId,out var target))Visit(target);
            marks[node.AppId]=2;ordered.Add(node);
        }
    }
    public static ApplicationDependencyNode[] Dependents(IEnumerable<ApplicationDependencyNode> nodes,string appId)
    {
        var ordered=Order(nodes);var affected=new HashSet<string>(StringComparer.Ordinal){appId};
        foreach(var node in ordered)if(node.Dependencies.Any(d=>affected.Contains(d.AppId)))affected.Add(node.AppId);
        return ordered.Reverse().Where(n=>affected.Contains(n.AppId)&&n.AppId!=appId).ToArray();
    }
}
