namespace LeanPlay.Core.Domain;

public enum ServiceObservedStatus
{
    Unknown = 0,
    Stopped,
    StartPending,
    StopPending,
    Running,
    ContinuePending,
    PausePending,
    Paused
}

public sealed record ServiceSnapshot(
    string ServiceName,
    ServiceObservedStatus OriginalStatus,
    IReadOnlyList<string> RunningDependentServices);

public sealed record PowerPlanSnapshot(Guid ActiveScheme);

public sealed record SystemSnapshot(
    PowerPlanSnapshot? PowerPlan,
    IReadOnlyList<ServiceSnapshot> Services)
{
    public ServiceSnapshot GetService(string name) =>
        Services.First(snapshot =>
            string.Equals(snapshot.ServiceName, name, StringComparison.OrdinalIgnoreCase));
}
