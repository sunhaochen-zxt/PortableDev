using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace PortableDev.Gui.Engine;

/// <summary>
/// Tray presence: closing the window hides it here instead of exiting, and the menu carries
/// the actions that make sense while the window is away (check/download updates, quit).
///
/// The icon is drawn at runtime: the project ships no .ico, and a tray icon must not depend
/// on a file that publish could forget to copy.
/// </summary>
public sealed class TrayHost : IDisposable
{
    private readonly NotifyIcon _notify;
    private readonly Icon _icon;

    public event Action? ShowRequested;
    public event Action? ExitRequested;
    public event Action? CheckUpdatesRequested;
    public event Action? DownloadUpdatesRequested;

    public TrayHost()
    {
        _icon = BuildIcon();

        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => ShowRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("检查更新", null, (_, _) => CheckUpdatesRequested?.Invoke());
        menu.Items.Add("下载可用更新", null, (_, _) => DownloadUpdatesRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

        _notify = new NotifyIcon
        {
            Icon = _icon,
            Text = "PortableDev",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _notify.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    public void Notify(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        try
        {
            _notify.ShowBalloonTip(4500, title, text, icon);
        }
        catch
        {
            // a tray balloon is advisory; never let it break the caller
        }
    }

    public void SetTooltip(string text)
    {
        try
        {
            _notify.Text = text.Length > 63 ? text[..60] + "..." : text;
        }
        catch
        {
            // NotifyIcon.Text has a hard length limit; ignore anything odd
        }
    }

    private static Icon BuildIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using var background = new SolidBrush(Color.FromArgb(0x4C, 0x9A, 0xFF));
            g.FillEllipse(background, 1, 1, 30, 30);

            using var font = new Font("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel);
            using var foreground = new SolidBrush(Color.White);
            var size = g.MeasureString("P", font);
            g.DrawString("P", font, foreground, (32 - size.Width) / 2f, (32 - size.Height) / 2f);
        }

        var handle = bitmap.GetHicon();
        try
        {
            // Clone from the handle so the bitmap can be disposed; the handle itself is
            // released by DestroyIcon below, which Icon.Dispose does not do.
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        try
        {
            _notify.Visible = false;
            _notify.Dispose();
        }
        catch
        {
            // shutting down anyway
        }

        _icon.Dispose();
    }
}
