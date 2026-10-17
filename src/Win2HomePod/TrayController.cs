namespace Win2HomePod;

internal sealed class TrayController : IDisposable
{
    private readonly NotifyIcon icon;
    private readonly Form form;
    public TrayController(Form form, Func<Task> connect, Func<Task> stop)
    {
        this.form = form;
        var menu = new ContextMenuStrip();
        menu.Items.Add("操作画面を開く", null, (_, _) => Show());
        menu.Items.Add("送信開始", null, async (_, _) => await connect());
        menu.Items.Add("送信停止", null, async (_, _) => await stop());
        menu.Items.Add("ログフォルダーを開く", null, (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", Program.State) { UseShellExecute = true }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => form.Close());
        icon = new NotifyIcon { Icon = SystemIcons.Application, Text = "Win2HomePod — 準備中", ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => Show();
        form.Resize += (_, _) => { if (form.WindowState == FormWindowState.Minimized) form.Hide(); };
    }
    public void Show()
    {
        form.Show();
        form.WindowState = FormWindowState.Normal;
        form.Activate();
    }
    public void Status(string text) => icon.Text = text[..Math.Min(text.Length, 63)];
    public void Hide() { form.WindowState = FormWindowState.Minimized; form.Hide(); }
    public void Dispose()
    {
        icon.Visible = false;
        icon.ContextMenuStrip?.Dispose();
        icon.Dispose();
    }
}
