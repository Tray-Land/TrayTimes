using System.Globalization;
using System.Text;

namespace TrayTimes.Diagnostics;

/// <summary>
/// An append-only text log that stays small: once the file would pass <see cref="MaxFileBytes"/> it
/// becomes the single archive (replacing the previous one) and a new file starts, so the two together
/// never hold much more than twice the limit. Writing never throws; a log that can't be written is
/// dropped rather than breaking the app.
/// </summary>
public sealed class RollingFileLog
{
    public const long DefaultMaxFileBytes = 128 * 1024;

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private long _length = -1;

    public RollingFileLog(string path, long maxFileBytes = DefaultMaxFileBytes, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileBytes);
        FilePath = path;
        ArchivePath = path + ".old";
        MaxFileBytes = maxFileBytes;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string FilePath { get; }

    /// <summary>Where the previous file goes when <see cref="FilePath"/> fills up.</summary>
    public string ArchivePath { get; }

    public long MaxFileBytes { get; }

    /// <summary>Adds one timestamped line. Line breaks in the message are flattened so one write is one line.</summary>
    public void Write(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string line = string.Create(CultureInfo.InvariantCulture,
            $"{_time.GetLocalNow():yyyy-MM-dd HH:mm:ss.fff} {message.ReplaceLineEndings(" ")}{Environment.NewLine}");
        int bytes = Encoding.UTF8.GetByteCount(line);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
                if (_length < 0)
                {
                    _length = File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0;
                }

                if (_length > 0 && _length + bytes > MaxFileBytes)
                {
                    File.Move(FilePath, ArchivePath, overwrite: true);
                    _length = 0;
                }

                File.AppendAllText(FilePath, line);
                _length += bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Measure the file again next time rather than trusting a count the failure may have broken.
                _length = -1;
                System.Diagnostics.Debug.WriteLine($"Couldn't write the log: {ex.GetType().Name}.");
            }
        }
    }
}
