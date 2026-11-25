using PortableDev.Core.Util;

namespace PortableDev.Core.Logging;

/// <summary>
/// Last-resort log used before <see cref="JsonlLogger"/> exists and when it fails
/// (kernel.md 7.1: `bootstrap.log` stays plaintext on purpose).
/// </summary>
public static class BootstrapLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Configure(string path) => _path = path;

    public static void Write(string message)
    {
        var line = $"{TimeUtil.NowIso()} {message}";
        try
        {
            lock (Gate)
            {
                if (_path is not null)
                {
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
            }
        }
        catch (IOException)
        {
            // Never throw from the last-resort log.
        }

        try
        {
            Console.Error.WriteLine(line);
        }
        catch (IOException)
        {
            // stderr may be closed when running detached; ignore.
        }
    }
}
