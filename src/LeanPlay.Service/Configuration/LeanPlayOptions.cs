using LeanPlay.Core.Domain;

namespace LeanPlay.Service.Configuration;

public sealed class LeanPlayOptions
{
    public const string SectionName = "LeanPlay";

    public string? DataDirectory { get; init; }

    public int ReconciliationIntervalSeconds { get; init; } = 5;

    public int RecoveryRetrySeconds { get; init; } = 10;

    public int ServiceTransitionTimeoutSeconds { get; init; } = 15;

    public List<GameProfileOptions> Profiles { get; init; } = new();
}

public sealed class GameProfileOptions
{
    public long? Id { get; init; }

    public string GameName { get; init; } = string.Empty;

    public string ExecutableName { get; init; } = string.Empty;

    public string? PowerPlanGuid { get; init; }

    public List<ServiceRuleOptions> ServiceRules { get; init; } = new();

    public GameProfile ToDomain() =>
        new(
            Id,
            GameName,
            ExecutableName,
            string.IsNullOrWhiteSpace(PowerPlanGuid) ? null : PowerPlanGuid,
            ServiceRules.Select(rule => rule.ToDomain()).ToArray());
}

public sealed class ServiceRuleOptions
{
    public string ServiceName { get; init; } = string.Empty;

    public ServiceDesiredState DesiredState { get; init; } = ServiceDesiredState.NoChange;

    public bool UserApproved { get; init; }

    public bool Required { get; init; }

    public ServiceRule ToDomain() =>
        new(ServiceName, DesiredState, UserApproved, Required);
}
