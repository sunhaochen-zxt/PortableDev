using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace PortableDev.Gui;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A crashed UI must still leave the environment usable: the CLI is the
        // fallback, so report rather than vanish.
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            TryLog(ex);
        };
    }

    private static bool _reported;

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryLog(e.Exception);

        // A modal dialog on every binding hiccup would make the window unusable and,
        // worse, would hide the window during startup. Report once, then keep going.
        if (!_reported)
        {
            _reported = true;
            var text =
                "界面遇到了一个未处理的错误，但已经写入的环境不受影响。\n\n" +
                e.Exception.Message + "\n\n" +
                "可以关掉窗口，改用 PortableDev.cmd plan / apply 继续。\n" +
                "详细信息已记录到 state\\logs\\gui-*.log。";

            Dispatcher.BeginInvoke(new Action(() =>
                System.Windows.MessageBox.Show(text, "PortableDev", MessageBoxButton.OK, MessageBoxImage.Warning)));
        }

        e.Handled = true;
    }

    private static void TryLog(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "..", "state", "logs");
            dir = Path.GetFullPath(dir);
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "gui-" + DateTime.Now.ToString("yyyyMMdd") + ".log"),
                DateTimeOffset.Now.ToString("O") + "  " + ex + Environment.NewLine + Environment.NewLine);
        }
        catch
        {
            // logging must never mask the original failure
        }
    }
}
