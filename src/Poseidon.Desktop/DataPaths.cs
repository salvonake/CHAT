namespace Poseidon.Desktop;

/// <summary>
/// Centralised paths used by all Desktop services.
/// Populated once in <see cref="App"/> startup.
/// </summary>
public sealed class DataPaths
{
    public required string DataDirectory { get; init; }
    public required string ModelsDirectory { get; init; }
    public string InstalledModelsDirectory { get; init; } = "";
    public required string VectorDbPath { get; init; }
    public required string HnswIndexPath { get; init; }
    public required string DocumentDbPath { get; init; }
    public required string AuditDbPath { get; init; }
    public required string WatchDirectory { get; init; }
    public string UserConfigPath { get; init; } = "";
    public string LogsDirectory { get; init; } = "";
    public string AppLogPath { get; init; } = "";
    public string StartupLogPath { get; init; } = "";
}

