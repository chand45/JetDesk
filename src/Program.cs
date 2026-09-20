using System.Text.Json;
using System.Windows.Forms;

namespace JetDesk;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 0) { Application.Run(new MainForm()); return 0; }
        try { return RunCommandAsync(args).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            var resultPath = Option(args, "--result");
            if (resultPath is not null) WriteJson(resultPath, new { Status = "error", Message = ex.Message });
            return 1;
        }
    }

    private static async Task<int> RunCommandAsync(string[] args)
    {
        using var worker = new AutomationWorker();
        var automation = new WindowsAutomation();
        var resultPath = Option(args, "--result");
        void Output(object value)
        {
            var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
            if (resultPath is not null) WriteJson(resultPath, value);
        }
        if (args.Contains("--list-windows")) { Output(await worker.InvokeAsync(automation.ListWindows)); return 0; }
        if (args.Contains("--list-apps")) { Output(await worker.InvokeAsync(automation.ListApps)); return 0; }
        long? target = long.TryParse(Option(args, "--window"), out var hwnd) ? hwnd : null;
        if (args.Contains("--observe")) { Output(await worker.InvokeAsync(() => automation.Observe(target))); return 0; }
        var goal = Option(args, "--run");
        if (goal is null)
        {
            Output(new { usage = "JetDesk.exe [--list-windows | --list-apps | --observe | --run \"request\"] [--window HWND] [--max-operations 50] [--result result.json] [--log-dir directory] [--cancel-after-seconds N]" });
            return 0;
        }
        using var client = new JevClient();
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        if (int.TryParse(Option(args, "--cancel-after-seconds"), out var seconds) && seconds > 0) cancellation.CancelAfter(TimeSpan.FromSeconds(seconds));
        var max = int.TryParse(Option(args, "--max-operations"), out var count) ? count : 50;
        var logDirectory = Option(args, "--log-dir") ?? Path.Combine(AppContext.BaseDirectory, "logs");
        var runner = new AgentRunner(automation, worker, client);
        runner.Progress += Console.WriteLine;
        var result = await runner.RunAsync(new RunOptions(goal, max, target), logDirectory, cancellation.Token);
        Output(result);
        return result.Status == "completed" ? 0 : result.Status is "limit_reached" or "stopped" ? 2 : 1;
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    private static void WriteJson(string path, object value)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
