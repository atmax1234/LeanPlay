# LeanPlay

LeanPlay is a Windows gaming-runtime experiment focused on measuring and temporarily reducing background interference without turning into a permanent “debloater.”

## Analyze this PC now

Double-click [Run LeanPlay Analysis.cmd](Run%20LeanPlay%20Analysis.cmd), approve the read-only kernel tracing prompt, and reproduce the performance problem for 60 seconds. LeanPlay opens a self-contained HTML report and writes the raw JSON beside it under `Documents\LeanPlay Reports`.

PowerShell users can label and extend the trace:

```powershell
.\scripts\Run-LeanPlayAnalysis.ps1 `
  -DurationSeconds 120 `
  -WorkloadLabel "CS2 stutter"
```

The analyzer records hardware and drivers, CPU/memory/disk/GPU activity, per-process resource use, gateway/public latency and jitter, TCP retransmissions, recent reliability events, and—when elevated—kernel disk/network plus DPC/ISR driver activity. It is strictly read-only. See [docs/ANALYZER.md](docs/ANALYZER.md) for interpretation and limitations.

The first implementation is deliberately conservative:

- detects CS2 and Valorant through passive WMI process start/stop traces;
- creates a durable, write-ahead snapshot before changing anything;
- can temporarily stop explicitly approved, non-critical services;
- can temporarily switch to an explicitly configured power plan;
- restores the exact captured state on normal exit, game crash, service shutdown, or service restart;
- records sessions and actions in SQLite;
- never reads game memory, injects code, hooks graphics APIs, or modifies anti-cheat processes.

No service rules or power-plan changes are enabled by default. This makes the checked-in configuration observation-only until the user makes an explicit choice.

## Build and test

```powershell
dotnet restore LeanPlay.sln
dotnet build LeanPlay.sln --configuration Release --no-restore
dotnet test LeanPlay.sln --configuration Release --no-build
```

Run the service interactively during development:

```powershell
dotnet run --project src/LeanPlay.Service
```

Force recovery from a surviving journal:

```powershell
dotnet run --project src/LeanPlay.Service -- --recover
```

The development defaults write application data beneath `data/runtime/`. A production Windows Service installation uses `%ProgramData%\LeanPlay` unless `DataDirectory` is configured.

After reviewing `appsettings.json`, an elevated PowerShell session can publish and install the service with:

```powershell
.\scripts\Install-LeanPlayService.ps1
```

The installer configures Service Control Manager restart actions at 5, 15, and 60 seconds so an unexpected host crash leads to journal recovery. `scripts/Recover-LeanPlay.ps1` is the explicit emergency recovery entry point. Installation is not performed automatically by the build.

## Safety boundary

LeanPlay only stops a service when all of the following are true:

1. the service is named in the active game profile;
2. `UserApproved` is `true`;
3. it is not on the built-in critical-service denylist;
4. it has no running dependent services;
5. its current state is safe to transition.

Access-denied and protected-service failures are audit events, not service-host crashes. Required rule failures abort activation and initiate rollback; optional failures leave that target unchanged.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the concrete design and [docs/MVP-IMPLEMENTATION.md](docs/MVP-IMPLEMENTATION.md) for scope and sequencing.
