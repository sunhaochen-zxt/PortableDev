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

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(options));
        return 0;
    }
}
