using System.ComponentModel;
using System.Runtime.InteropServices;
using LeanPlay.Core.Abstractions;
using LeanPlay.Core.Domain;

namespace LeanPlay.Service.Windows;

public sealed partial class WindowsPowerPlanController : IPowerPlanController
{
    public Task<PowerPlanSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();

        var result = PowerGetActiveScheme(0, out var schemePointer);
        ThrowIfError(result, nameof(PowerGetActiveScheme));
        if (schemePointer == 0)
        {
            throw new InvalidOperationException(
                "PowerGetActiveScheme returned an empty scheme pointer.");
        }

        try
        {
            return Task.FromResult(
                new PowerPlanSnapshot(Marshal.PtrToStructure<Guid>(schemePointer)));
        }
        finally
        {
            _ = LocalFree(schemePointer);
        }
    }

    public Task SetActiveAsync(Guid scheme, CancellationToken cancellationToken)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        var mutableScheme = scheme;
        ThrowIfError(
            PowerSetActiveScheme(0, ref mutableScheme),
            nameof(PowerSetActiveScheme));
        return Task.CompletedTask;
    }

    public Task RestoreAsync(
        PowerPlanSnapshot snapshot,
        CancellationToken cancellationToken) =>
        SetActiveAsync(snapshot.ActiveScheme, cancellationToken);

    private static void ThrowIfError(uint result, string operation)
    {
        if (result != 0)
        {
            throw new Win32Exception((int)result, $"{operation} failed.");
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Power-plan control is available only on Windows.");
        }
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(
        nint userRootPowerKey,
        out nint activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSetActiveScheme(
        nint userRootPowerKey,
        ref Guid schemeGuid);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
