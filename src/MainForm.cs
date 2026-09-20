using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace JevDesktop;

public sealed class MainForm : Form
{
    private readonly AutomationWorker worker = new();
    private readonly WindowsAutomation automation = new();
    private readonly TextBox request = new() { Multiline = true, Height = 82, Dock = DockStyle.Fill, AccessibleName = "Request", PlaceholderText = "For example: play Sweater Weather on Spotify" };
    private readonly ComboBox target = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Starting application" };
    private readonly NumericUpDown limit = new() { Minimum = 1, Maximum = 500, Value = 50, Width = 75, AccessibleName = "Maximum operations" };
    private readonly Button start = new() { Text = "Run request", AutoSize = true, AccessibleName = "Run request" };
    private readonly Button stop = new() { Text = "Stop · F8", AutoSize = true, Enabled = false, AccessibleName = "Stop" };
    private readonly Button refresh = new() { Text = "Refresh apps", AutoSize = true };
    private readonly Button openLogs = new() { Text = "Open logs", AutoSize = true };
    private readonly Label status = new() { AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Text = "Ready", Dock = DockStyle.Fill, AccessibleName = "Run status" };
    private readonly Label observation = new() { AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Text = "No screen observed yet", Dock = DockStyle.Fill };
    private readonly RichTextBox activity = new() { ReadOnly = true, Dock = DockStyle.Fill, BackColor = Color.FromArgb(245, 247, 250), BorderStyle = BorderStyle.None, AccessibleName = "Action log", Font = new Font("Consolas", 10) };
    private CancellationTokenSource? cancellation;
    private readonly string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
    private bool running;

    public MainForm()
    {
        Text = "Jev Desktop";
        ClientSize = new Size(900, 680);
        MinimumSize = new Size(740, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 9 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.Controls.Add(new Label { Text = "Jev Desktop", Font = new Font("Segoe UI", 23, FontStyle.Bold), AutoSize = true }, 0, 0);
        layout.Controls.Add(new Label { Text = "Tell Jev what to do. It reads controls, chooses an action, and checks the next screen.", AutoSize = true }, 0, 1);
        layout.Controls.Add(request, 0, 2);
        var appsRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        appsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        appsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        appsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        appsRow.Controls.Add(new Label { Text = "Start in", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 0);
        appsRow.Controls.Add(target, 1, 0); appsRow.Controls.Add(refresh, 2, 0);
        layout.Controls.Add(appsRow, 0, 3);
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        controls.Controls.Add(start); controls.Controls.Add(stop);
        controls.Controls.Add(new Label { Text = "   Operation limit", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        controls.Controls.Add(limit); controls.Controls.Add(openLogs);
        layout.Controls.Add(controls, 0, 4);
        layout.Controls.Add(status, 0, 5); layout.Controls.Add(observation, 0, 6); layout.Controls.Add(activity, 0, 7);
        layout.Controls.Add(new Label { Text = "Uses JEV_KEY. Sends observed UI text to TypeSafe. Stop with F8; avoid typing while a run is active.", AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(0, 10, 0, 0) }, 0, 8);
        Controls.Add(layout);
        AcceptButton = start;
        start.Click += async (_, _) => await StartRun();
        stop.Click += (_, _) => cancellation?.Cancel();
        refresh.Click += async (_, _) => await RefreshWindows();
        openLogs.Click += (_, _) => {
            Directory.CreateDirectory(logDirectory);
            var explorer = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            explorer.ArgumentList.Add(logDirectory);
            System.Diagnostics.Process.Start(explorer);
        };
        Shown += async (_, _) => {
            if (!RegisterHotKey(Handle, 1, 0x4000, 0x77)) { stop.Text = "Stop"; Append("F8 is in use by another application. Use the Stop button to cancel."); }
            await RefreshWindows();
        };
        FormClosing += (_, e) => {
            if (running) { cancellation?.Cancel(); e.Cancel = true; Append("Stopping the active run. Close again once it has stopped."); }
        };
        FormClosed += (_, _) => { UnregisterHotKey(Handle, 1); worker.Dispose(); };
    }

    private async Task RefreshWindows()
    {
        try
        {
            var previous = (target.SelectedItem as WindowChoice)?.Handle;
            var windows = await worker.InvokeAsync(automation.ListWindows);
            target.Items.Clear(); target.Items.Add(new WindowChoice(null, "Automatic — Jev can choose an open app or launch one"));
            foreach (var window in windows) target.Items.Add(new WindowChoice(window.Handle, $"{window.ProcessName} — {window.Title}"));
            target.SelectedIndex = 0;
            if (previous is not null)
                for (var i = 0; i < target.Items.Count; i++) if (((WindowChoice)target.Items[i]!).Handle == previous) target.SelectedIndex = i;
            status.Text = "Ready · " + windows.Count + " open applications";
        }
        catch (Exception ex) { status.Text = "Could not list apps"; Append(ex.Message); }
    }

    private async Task StartRun()
    {
        if (running) return;
        if (string.IsNullOrWhiteSpace(request.Text)) { request.Focus(); status.Text = "Enter a request first"; return; }
        try
        {
            using var client = new JevClient();
            cancellation = new CancellationTokenSource();
            running = true; SetRunning(true); activity.Clear();
            var options = new RunOptions(request.Text.Trim(), (int)limit.Value, (target.SelectedItem as WindowChoice)?.Handle);
            var runner = new AgentRunner(automation, worker, client);
            runner.Progress += message => Ui(() => { status.Text = message; Append(message); });
            runner.Observed += snapshot => Ui(() => observation.Text = $"Screen: {snapshot.Window.Title} · {snapshot.Controls.Count} controls{(snapshot.Truncated ? " · bounded snapshot" : "")}");
            Append("Starting in 3 seconds. F8 stops the run.");
            await Task.Delay(3000, cancellation.Token);
            var result = await runner.RunAsync(options, logDirectory, cancellation.Token);
            status.Text = result.Status switch { "completed" => "✓ " + result.Message, "error" => "Error: " + result.Message, _ => result.Message };
            Append("Log: " + result.LogPath);
        }
        catch (OperationCanceledException) { status.Text = "Stopped"; Append("Stopped before input."); }
        catch (Exception ex) { status.Text = "Could not start"; Append(ex.Message); }
        finally { running = false; SetRunning(false); cancellation?.Dispose(); cancellation = null; }
    }

    private void SetRunning(bool value) { start.Enabled = !value; stop.Enabled = value; request.Enabled = !value; target.Enabled = !value; refresh.Enabled = !value; limit.Enabled = !value; }
    private void Append(string message) { activity.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}"); activity.SelectionStart = activity.TextLength; activity.ScrollToCaret(); }
    private void Ui(Action action) { if (!IsDisposed && IsHandleCreated) BeginInvoke(action); }
    protected override void WndProc(ref Message m)
    { if (m.Msg == 0x0312 && m.WParam.ToInt32() == 1) cancellation?.Cancel(); base.WndProc(ref m); }
    private sealed record WindowChoice(long? Handle, string Label) { public override string ToString() => Label; }
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
