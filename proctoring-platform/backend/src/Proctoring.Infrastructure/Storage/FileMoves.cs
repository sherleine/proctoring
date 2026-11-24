namespace Proctoring.Infrastructure.Storage;

/// <summary>
/// File.Move with a short retry. On Windows, antivirus scanners and the search indexer briefly open files
/// that were just written, which makes an immediate rename fail with "access denied" or a sharing violation.
/// A few quick retries make that transient condition invisible; real errors still surface.
/// </summary>
internal static class FileMoves
{
    public static async Task MoveAsync(string source, string destination, bool overwrite, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite);
                return;
            }
            catch (Exception ex) when (attempt < 4 && ex is UnauthorizedAccessException or IOException && !IsAlreadyExists(ex, destination, overwrite))
            {
                await Task.Delay(50 * attempt, ct);
            }
        }
    }

    /// <summary>A destination that already exists (without overwrite) is a real conflict, not a transient lock.</summary>
    private static bool IsAlreadyExists(Exception ex, string destination, bool overwrite) =>
        !overwrite && ex is IOException && File.Exists(destination);
}
