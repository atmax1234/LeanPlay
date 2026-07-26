using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace LeanPlay.Service.Configuration;

public sealed class RuntimePaths
{
    public RuntimePaths(IOptions<LeanPlayOptions> options)
    {
        var configured = options.Value.DataDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            DataDirectory = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(configured));
        }
        else if (WindowsServiceHelpers.IsWindowsService())
        {
            DataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "LeanPlay");
        }
        else
        {
            DataDirectory = Path.Combine(AppContext.BaseDirectory, "data", "runtime");
        }

        JournalPath = Path.Combine(DataDirectory, "recovery-journal.json");
        DatabasePath = Path.Combine(DataDirectory, "leanplay_mvp.db");
        SchemaPath = Path.Combine(AppContext.BaseDirectory, "data", "schema.sql");
    }

    public string DataDirectory { get; }

    public string JournalPath { get; }

    public string DatabasePath { get; }

    public string SchemaPath { get; }
}
