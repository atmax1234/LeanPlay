using LeanPlay.Core.Domain;

namespace LeanPlay.Core.Abstractions;

public interface IServiceStateController
{
    Task<ServiceSnapshot> CaptureAsync(string serviceName, CancellationToken cancellationToken);

    Task StopAsync(string serviceName, CancellationToken cancellationToken);

    Task RestoreAsync(ServiceSnapshot snapshot, CancellationToken cancellationToken);
}

public interface IPowerPlanController
{
    Task<PowerPlanSnapshot> CaptureAsync(CancellationToken cancellationToken);

    Task SetActiveAsync(Guid scheme, CancellationToken cancellationToken);

    Task RestoreAsync(PowerPlanSnapshot snapshot, CancellationToken cancellationToken);
}
