using System.Security.Cryptography;
using System.Text;
using PortableDev.Window;

namespace PortableDev.Window;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = WindowOptions.Parse(args);
        if (options.SelfTest is not null)
        {
            return SelfTest.RunAsync(options).GetAwaiter().GetResult();
        }

        // One window per stick: a second launch just exits (smoke runs may overlap on purpose).
        Mutex? mutex = null;
        if (options.Smoke is null)
        {
            mutex = new Mutex(true, "PortableDev.Window." + RootKey(options.Root), out var created);
            if (!created)
            {
                mutex.Dispose();
                return 0;
            }
        }

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm(options));
            return 0;
        }
        finally
        {
            mutex?.Dispose();
        }
    }

    private static string RootKey(string root)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(root.TrimEnd(Path.DirectorySeparatorChar)));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }
}
