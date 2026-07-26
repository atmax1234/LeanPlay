namespace LeanPlay.Core.Domain;

public enum ServiceDesiredState
{
    NoChange = 0,
    Stop = 1
}

public sealed record ServiceRule(
    string ServiceName,
    ServiceDesiredState DesiredState,
    bool UserApproved,
    bool Required = false);

public sealed record GameProfile(
    long? Id,
    string GameName,
    string ExecutableName,
    string? PowerPlanGuid,
    IReadOnlyList<ServiceRule> ServiceRules)
{
    public string NormalizedExecutableName =>
        Path.GetFileName(ExecutableName).Trim().ToUpperInvariant();
}
