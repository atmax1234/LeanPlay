using System.ComponentModel;
using System.ServiceProcess;
using LeanPlay.Core.Abstractions;
using LeanPlay.Core.Domain;
using LeanPlay.Service.Configuration;
using Microsoft.Extensions.Options;

namespace LeanPlay.Service.Windows;

public sealed class WindowsServiceStateController : IServiceStateController
{
    private readonly TimeSpan _transitionTimeout;

    public WindowsServiceStateController(IOptions<LeanPlayOptions> options)
    {
        var seconds = Math.Clamp(options.Value.ServiceTransitionTimeoutSeconds, 1, 120);
        _transitionTimeout = TimeSpan.FromSeconds(seconds);
    }

    public Task<ServiceSnapshot> CaptureAsync(
        string serviceName,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();

        using var controller = Open(serviceName);
        controller.Refresh();
        var status = Map(controller.Status);
        if (status is ServiceObservedStatus.StartPending
            or ServiceObservedStatus.StopPending
            or ServiceObservedStatus.ContinuePending
            or ServiceObservedStatus.PausePending
            or ServiceObservedStatus.Unknown)
        {
            throw new InvalidOperationException(
                $"Service '{serviceName}' is transitioning ({status}).");
        }

        var dependents = new List<string>();
        foreach (var dependent in controller.DependentServices)
        {
            using (dependent)
            {
                dependent.Refresh();
                if (dependent.Status is ServiceControllerStatus.Running
                    or ServiceControllerStatus.StartPending
                    or ServiceControllerStatus.ContinuePending)
                {
                    dependents.Add(dependent.ServiceName);
                }
            }
        }

        return Task.FromResult(
            new ServiceSnapshot(controller.ServiceName, status, dependents));
    }

    public async Task StopAsync(string serviceName, CancellationToken cancellationToken)
    {
        EnsureWindows();
        using var controller = Open(serviceName);
        controller.Refresh();

        if (controller.Status == ServiceControllerStatus.Stopped)
        {
            return;
        }

        if (controller.Status != ServiceControllerStatus.StopPending)
        {
            if (!controller.CanStop)
            {
                throw new InvalidOperationException(
                    $"Service '{serviceName}' does not accept stop commands.");
            }

            controller.Stop();
        }

        await WaitForStatusAsync(
            controller,
            ServiceControllerStatus.Stopped,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreAsync(
        ServiceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        using var controller = Open(snapshot.ServiceName);
        controller.Refresh();

        switch (snapshot.OriginalStatus)
        {
            case ServiceObservedStatus.Running:
                await EnsureRunningAsync(controller, cancellationToken).ConfigureAwait(false);
                break;
            case ServiceObservedStatus.Paused:
                await EnsureRunningAsync(controller, cancellationToken).ConfigureAwait(false);
                controller.Refresh();
                if (controller.Status != ServiceControllerStatus.Paused)
                {
                    if (!controller.CanPauseAndContinue)
                    {
                        throw new InvalidOperationException(
                            $"Service '{snapshot.ServiceName}' cannot be returned to paused state.");
                    }

                    controller.Pause();
                    await WaitForStatusAsync(
                        controller,
                        ServiceControllerStatus.Paused,
                        cancellationToken).ConfigureAwait(false);
                }

                break;
            case ServiceObservedStatus.Stopped:
                if (controller.Status != ServiceControllerStatus.Stopped)
                {
                    if (controller.Status != ServiceControllerStatus.StopPending)
                    {
                        controller.Stop();
                    }

                    await WaitForStatusAsync(
                        controller,
                        ServiceControllerStatus.Stopped,
                        cancellationToken).ConfigureAwait(false);
                }

                break;
            default:
                throw new InvalidOperationException(
                    $"Cannot restore service '{snapshot.ServiceName}' to " +
                    $"{snapshot.OriginalStatus}.");
        }
    }

    private async Task EnsureRunningAsync(
        ServiceController controller,
        CancellationToken cancellationToken)
    {
        controller.Refresh();
        if (controller.Status == ServiceControllerStatus.Paused)
        {
            controller.Continue();
        }
        else if (controller.Status is ServiceControllerStatus.Stopped
                 or ServiceControllerStatus.StopPending)
        {
            if (controller.Status == ServiceControllerStatus.StopPending)
            {
                await WaitForStatusAsync(
                    controller,
                    ServiceControllerStatus.Stopped,
                    cancellationToken).ConfigureAwait(false);
            }

            controller.Start();
        }

        await WaitForStatusAsync(
            controller,
            ServiceControllerStatus.Running,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForStatusAsync(
        ServiceController controller,
        ServiceControllerStatus expected,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _transitionTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            controller.Refresh();
            if (controller.Status == expected)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken)
                .ConfigureAwait(false);
        }

        controller.Refresh();
        throw new System.TimeoutException(
            $"Service '{controller.ServiceName}' did not reach {expected} within " +
            $"{_transitionTimeout.TotalSeconds:F0} seconds; current state is " +
            $"{controller.Status}.");
    }

    private static ServiceController Open(string serviceName)
    {
        try
        {
            var controller = new ServiceController(serviceName);
            _ = controller.Status;
            return controller;
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"Service '{serviceName}' does not exist or cannot be queried.",
                exception);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"Service '{serviceName}' cannot be queried: {exception.Message}",
                exception);
        }
    }

    private static ServiceObservedStatus Map(ServiceControllerStatus status) =>
        status switch
        {
            ServiceControllerStatus.Stopped => ServiceObservedStatus.Stopped,
            ServiceControllerStatus.StartPending => ServiceObservedStatus.StartPending,
            ServiceControllerStatus.StopPending => ServiceObservedStatus.StopPending,
            ServiceControllerStatus.Running => ServiceObservedStatus.Running,
            ServiceControllerStatus.ContinuePending => ServiceObservedStatus.ContinuePending,
            ServiceControllerStatus.PausePending => ServiceObservedStatus.PausePending,
            ServiceControllerStatus.Paused => ServiceObservedStatus.Paused,
            _ => ServiceObservedStatus.Unknown
        };

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows service control is available only on Windows.");
        }
    }
}
