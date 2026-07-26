using LeanPlay.Core.Domain;
using LeanPlay.Core.Engine;
using LeanPlay.Service.Configuration;
using Microsoft.Extensions.Options;

namespace LeanPlay.Service.Runtime;

public sealed class ProfileCatalog
{
    private readonly IReadOnlyDictionary<string, GameProfile> _profiles;

    public ProfileCatalog(IOptions<LeanPlayOptions> options)
    {
        var profiles = options.Value.Profiles.Select(profile => profile.ToDomain()).ToArray();
        foreach (var profile in profiles)
        {
            OptimizationPolicy.Validate(profile);
        }

        _profiles = profiles.ToDictionary(
            profile => profile.NormalizedExecutableName,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<GameProfile> All => _profiles.Values.ToArray();

    public bool TryGet(string executableName, out GameProfile profile) =>
        _profiles.TryGetValue(
            Path.GetFileName(executableName).Trim().ToUpperInvariant(),
            out profile!);
}
