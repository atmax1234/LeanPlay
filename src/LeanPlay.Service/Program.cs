using LeanPlay.Core.Abstractions;
using LeanPlay.Core.Engine;
using LeanPlay.Core.Persistence;
using LeanPlay.Service.Configuration;
using LeanPlay.Service.Monitoring;
using LeanPlay.Service.Persistence;
using LeanPlay.Service.Runtime;
using LeanPlay.Service.Windows;

var builder = Host.CreateApplicationBuilder(
    new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory
    });
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "LeanPlay Runtime";
});
builder.Services
    .AddOptions<LeanPlayOptions>()
    .Bind(builder.Configuration.GetSection(LeanPlayOptions.SectionName))
    .Validate(
        options => options.Profiles.Count > 0,
        "At least one game profile must be configured.")
    .ValidateOnStart();

builder.Services.AddSingleton<RuntimePaths>();
builder.Services.AddSingleton<ProfileCatalog>();
builder.Services.AddSingleton<SqliteStore>();
builder.Services.AddSingleton<IAuditSink>(
    services => services.GetRequiredService<SqliteStore>());
builder.Services.AddSingleton<IServiceStateController, WindowsServiceStateController>();
builder.Services.AddSingleton<IPowerPlanController, WindowsPowerPlanController>();
builder.Services.AddSingleton<IRecoveryJournalStore>(services =>
{
    var paths = services.GetRequiredService<RuntimePaths>();
    return new FileRecoveryJournalStore(paths.JournalPath);
});
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<OptimizationCoordinator>();
builder.Services.AddHostedService<GameDetectionWorker>();

using var host = builder.Build();
if (args.Any(argument =>
        string.Equals(argument, "--recover", StringComparison.OrdinalIgnoreCase)))
{
    var database = host.Services.GetRequiredService<SqliteStore>();
    await database.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
    var coordinator = host.Services.GetRequiredService<OptimizationCoordinator>();
    var result = await coordinator
        .RecoverIfRequiredAsync(CancellationToken.None)
        .ConfigureAwait(false);

    if (result.Restored)
    {
        Console.WriteLine(
            result.JournalFound
                ? $"LeanPlay recovery completed for session {result.SessionId}."
                : "LeanPlay recovery found no pending journal.");
        return;
    }

    Console.Error.WriteLine(
        $"LeanPlay recovery is incomplete: {string.Join("; ", result.Errors)}");
    Environment.ExitCode = 2;
    return;
}

await host.RunAsync().ConfigureAwait(false);
