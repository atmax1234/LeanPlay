# LeanPlay Windows Analyzer

The analyzer is the read-only diagnostic half of LeanPlay. It is usable independently of the Windows service and does not apply optimizations.

## Run it

Double-click `Run LeanPlay Analysis.cmd`, approve the elevation prompt, and reproduce the slowdown for the next 60 seconds. The resulting HTML report opens automatically and the matching JSON data is saved under `Documents\LeanPlay Reports`.

From PowerShell:

```powershell
.\scripts\Run-LeanPlayAnalysis.ps1 `
  -DurationSeconds 120 `
  -WorkloadLabel "CS2 stutter"
```

Useful alternatives:

```powershell
# Standard-user trace without kernel ETW
.\scripts\Run-LeanPlayAnalysis.ps1 -DurationSeconds 60 -NoElevation

# Run the already-published executable directly
.\artifacts\analyzer\LeanPlay.Analyzer.exe `
  --duration 120 `
  --label "Valorant match" `
  --target 1.1.1.1,8.8.8.8
```

The launch script publishes the analyzer automatically when the executable is absent.

## What it measures

### Always available

- CPU total and privileged utilization through native system-time deltas;
- available physical memory and commit pressure through `GetPerformanceInfo`;
- process CPU, working set, private bytes, thread count, and process I/O deltas;
- GPU engine activity through Windows GPU performance counters;
- adapter traffic, TCP retransmissions, and active per-process TCP/UDP endpoint counts;
- default-gateway and public-target ping, loss, and jitter;
- CPU, GPU, RAM, motherboard, BIOS, disk, volume, NIC, and driver inventory;
- NVIDIA temperature, power, and exact VRAM when `nvidia-smi` is available;
- power plan, HAGS, Game Mode, VBS/Memory Integrity, Defender, pending reboot;
- relevant System log errors and warnings from the previous seven days, including
  WHEA, display/storage resets, UDP port exhaustion, and repeated link drops;
- active launchers, synchronization clients, overlays, and capture tools.

### Elevated kernel ETW

- exact per-process disk reads and writes;
- exact per-process TCP/IP send and receive bytes;
- DPC and ISR event counts and maximum execution time;
- driver/image attribution for interrupt routines when Windows image events resolve them.

Elevation is requested only for read-only ETW collection. The analyzer does not stop services, change registry values, adjust priority, or apply a power plan.

## Reading the report

Findings are ordered by severity and confidence:

- **Critical:** measured evidence that can directly explain severe stalls, loss, or instability.
- **Warning:** a repeatable candidate that deserves an isolated test.
- **Information:** relevant inventory or activity without evidence of harm.
- **Good:** a subsystem had measurable headroom during the trace.

Each finding contains its raw evidence, a bounded interpretation, and one next action. “Good” means only that the problem did not occur in the measured interval.

For a useful result:

1. restart first if the report says a reboot is pending;
2. close this documentation and start the actual game/workload;
3. launch a 120-second elevated analysis;
4. reproduce the exact stutter, latency spike, or slowdown;
5. keep the HTML and JSON pair;
6. repeat after changing only one measured suspect.

Do not compare two different maps, game builds, thermal states, or background download conditions and call the result causal.

## Limitations

- ICMP can be blocked or deprioritized and is not identical to a game’s UDP traffic.
- Process I/O fallback includes device I/O; elevated ETW is required for exact disk attribution.
- GPU utilization alone cannot diagnose a frame-time bottleneck.
- CPU temperature is intentionally not guessed because Windows exposes no universal reliable sensor API.
- The analyzer reports System log history, so an old event may not overlap the trace.
- Anti-cheat compatibility remains external: no game memory, injection, graphics hook, or protected-process manipulation.
