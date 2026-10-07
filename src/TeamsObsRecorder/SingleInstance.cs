namespace TeamsObsRecorder;

/// <summary>
/// One running copy per user, held as an exclusive lock on a file in the config folder.
/// (A named Mutex on Linux is scoped to the terminal session, so a copy started at login and
/// one started from a terminal wouldn't see each other.)
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly FileStream? _lock;

    public bool IsFirst => _lock is not null;

    public SingleInstance(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            _lock = new FileStream(Path.Combine(directory, "running.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            _lock = null; // another copy holds it
        }
    }

    public void Dispose() => _lock?.Dispose();
}
