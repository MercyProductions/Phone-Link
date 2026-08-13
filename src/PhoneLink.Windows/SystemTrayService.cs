namespace PhoneLink.Windows;

public sealed class SystemTrayService : IDisposable
{
    private readonly MainWindow _window;
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private bool _disposed;

    public SystemTrayService(MainWindow window)
    {
        _window = window;
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Phone Link", image: null, (_, _) => ShowWindow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", image: null, (_, _) => App.RequestExit());

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = LoadIcon(),
            Text = "Phone Link",
            Visible = true,
        };
        _notifyIcon.DoubleClick += NotifyIcon_DoubleClick;
    }

    public void ShowWindow() =>
        _window.DispatcherQueue.TryEnqueue(_window.ShowFromTray);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.DoubleClick -= NotifyIcon_DoubleClick;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
    }

    private void NotifyIcon_DoubleClick(object? sender, EventArgs args) => ShowWindow();

    private static System.Drawing.Icon LoadIcon()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
            Path.Combine(AppContext.BaseDirectory, "AppX", "Assets", "AppIcon.ico"),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return new System.Drawing.Icon(candidate);
            }
        }

        var processIcon = Environment.ProcessPath is string processPath
            ? System.Drawing.Icon.ExtractAssociatedIcon(processPath)
            : null;
        return processIcon ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }
}
