using RpfToFiveM.Core.Crypto;

namespace RpfToFiveM.Tests.Fixtures;

/// <summary>Integration test that only runs when a GTA V install is present.</summary>
public sealed class GameInstalledFactAttribute : FactAttribute
{
    public static readonly string? GameFolder = GameLocator.FindGameFolders().FirstOrDefault();

    public GameInstalledFactAttribute()
    {
        if (GameFolder is null) Skip = "GTA V is not installed on this machine.";
    }
}
