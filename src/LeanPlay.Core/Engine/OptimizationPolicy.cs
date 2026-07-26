using System.Text.RegularExpressions;
using LeanPlay.Core.Domain;

namespace LeanPlay.Core.Engine;

public sealed partial class OptimizationPolicy
{
    private static readonly HashSet<string> ProtectedServices = new(
        new[]
        {
            "Appinfo",
            "BFE",
            "BrokerInfrastructure",
            "CoreMessagingRegistrar",
            "CryptSvc",
            "DcomLaunch",
            "Dhcp",
            "Dnscache",
            "EventLog",
            "EventSystem",
            "gpsvc",
            "LSM",
            "mpssvc",
            "NlaSvc",
            "nsi",
            "PlugPlay",
            "Power",
            "ProfSvc",
            "RpcEptMapper",
            "RpcSs",
            "SamSs",
            "Schedule",
            "SENS",
            "SystemEventsBroker",
            "VGC",
            "WinDefend",
            "Winmgmt"
        },
        StringComparer.OrdinalIgnoreCase);

    public static void Validate(GameProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (string.IsNullOrWhiteSpace(profile.GameName))
        {
            throw new ProfileValidationException("A game profile must have a name.");
        }

        if (string.IsNullOrWhiteSpace(profile.ExecutableName) ||
            !string.Equals(
                profile.ExecutableName,
                Path.GetFileName(profile.ExecutableName),
                StringComparison.Ordinal))
        {
            throw new ProfileValidationException(
                "ExecutableName must be a file name, not a path.");
        }

        if (!profile.ExecutableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProfileValidationException("ExecutableName must end in .exe.");
        }

        if (profile.PowerPlanGuid is not null &&
            !Guid.TryParse(profile.PowerPlanGuid, out _))
        {
            throw new ProfileValidationException("PowerPlanGuid is not a valid GUID.");
        }

        var duplicate = profile.ServiceRules
            .GroupBy(rule => rule.ServiceName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new ProfileValidationException(
                $"Service '{duplicate.Key}' appears more than once.");
        }

        foreach (var rule in profile.ServiceRules)
        {
            Validate(rule);
        }
    }

    private static void Validate(ServiceRule rule)
    {
        if (!ServiceNamePattern().IsMatch(rule.ServiceName))
        {
            throw new ProfileValidationException(
                $"Service name '{rule.ServiceName}' contains invalid characters.");
        }

        if (rule.DesiredState == ServiceDesiredState.NoChange)
        {
            return;
        }

        if (!rule.UserApproved)
        {
            throw new ProfileValidationException(
                $"Service '{rule.ServiceName}' was not explicitly approved.");
        }

        if (ProtectedServices.Contains(rule.ServiceName))
        {
            throw new ProfileValidationException(
                $"Service '{rule.ServiceName}' is protected by LeanPlay policy.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,256}$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceNamePattern();
}
