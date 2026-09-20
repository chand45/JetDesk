using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JetDesk;

/// <summary>One MTA thread owns every live UIA object and action.</summary>
public sealed class AutomationWorker : IDisposable
{
    private readonly BlockingCollection<Action> queue = new();
    private readonly Thread thread;
    public AutomationWorker()
    {
        thread = new Thread(() => { foreach (var action in queue.GetConsumingEnumerable()) action(); })
        { IsBackground = true, Name = "JetDesk UI Automation" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }
    public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken ct = default)
    {
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Add(() => {
            if (ct.IsCancellationRequested) { source.TrySetCanceled(ct); return; }
            try { source.TrySetResult(action()); }
            catch (Exception ex) { source.TrySetException(ex); }
        }, ct);
        return source.Task.WaitAsync(ct);
    }
    public void Dispose() => queue.CompleteAdding();
}

public sealed class AgentRunner
{
    private readonly WindowsAutomation automation;
    private readonly AutomationWorker worker;
    private readonly JevClient client;
    public event Action<string>? Progress;
    public event Action<Snapshot>? Observed;
    public AgentRunner(WindowsAutomation automation, AutomationWorker worker, JevClient client)
    { this.automation = automation; this.worker = worker; this.client = client; }

    public async Task<RunResult> RunAsync(RunOptions options, string logDirectory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Goal)) throw new ArgumentException("Enter a request first.");
        if (options.MaxOperations is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(options.MaxOperations));
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, $"run-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.jsonl");
        using var log = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        var history = new List<StepRecord>();
        var operations = 0;
        void Record(string kind, object data) => log.WriteLine(JsonSerializer.Serialize(new { timestamp = DateTimeOffset.Now, kind, data }));
        void Say(string message) { Record("progress", new { message }); Progress?.Invoke(message); }
        RunResult Finish(string status, string message)
        {
            var result = new RunResult(status, operations, message, logPath);
            Record("result", result); Progress?.Invoke(message); return result;
        }
        async Task<Snapshot> Observe()
        {
            var snapshot = await worker.InvokeAsync(() => automation.Observe(), ct);
            // Password fields are excluded by the backend; no API key or authorization headers are logged.
            Record("observation", snapshot); Observed?.Invoke(snapshot); return snapshot;
        }
        Record("start", new { options.Goal, options.MaxOperations, options.TargetHandle });
        try
        {
            await worker.InvokeAsync(() => { automation.TargetHandle = options.TargetHandle; return true; }, ct);
            string? lastAction = null, lastState = null;
            var repeats = 0;
            while (operations < options.MaxOperations)
            {
                ct.ThrowIfCancellationRequested();
                Say($"Observing screen · action {operations + 1}/{options.MaxOperations}");
                var snapshot = await Observe();
                // Check the outcome before proposing another input. The action classifier
                // can keep preferring a visible Play/Search button after the task succeeds.
                if (operations > 0)
                {
                    Say("Checking the result on the fresh screen…");
                    var outcomeCheck = await client.VerifyAsync(options.Goal, snapshot, history, ct);
                    Record("verification", outcomeCheck);
                    if (outcomeCheck.Achieved)
                        return Finish("completed", $"Goal achieved after {operations} actions. {outcomeCheck.Evidence}");
                }
                Say($"{snapshot.Window.Title}: {snapshot.Controls.Count} controls. Asking Jev…");
                var candidates = TextCandidates.Build(options.Goal, snapshot);
                var decision = await client.DecideAsync(options.Goal, snapshot, history, candidates, ct);
                Record("decision", decision);
                Say($"Jev: {decision.Operation} {decision.TargetId ?? ""} · confidence {decision.Confidence:P0}");

                if (decision.GoalAchieved || decision.Operation == "done")
                {
                    Say("Jev reports completion. Checking a fresh screen…");
                    await Task.Delay(options.SettleMilliseconds, ct);
                    var check = await client.VerifyAsync(options.Goal, await Observe(), history, ct);
                    Record("verification", check);
                    if (check.Achieved) return Finish("completed", $"Goal achieved after {operations} actions. {check.Evidence}");
                    Say("Fresh-screen verification did not confirm completion; continuing.");
                    history.Add(new StepRecord(++operations, "completion_rejected", "", null, check.Evidence));
                    continue;
                }
                if (decision.Operation == "blocked")
                    return Finish("needs_attention", decision.Evidence ?? "Jev could not find a suitable next action in the available controls.");

                if (decision.Confidence < options.MinimumConfidence && decision.Operation != "wait")
                {
                    Say("Choice is uncertain; refreshing before making an input.");
                    history.Add(new StepRecord(++operations, "wait", "", null, "Low confidence; no input made. Choose a clearer action or wait."));
                    await Task.Delay(options.SettleMilliseconds, ct);
                    continue;
                }
                string? text = null;
                if (decision.Operation == "set_text")
                {
                    text = candidates.FirstOrDefault(c => c.Id == decision.TextId)?.Text;
                    if (text is null) throw new InvalidOperationException("Jev selected an unknown text candidate; input was not sent.");
                }
                var selectedControl = snapshot.Controls.FirstOrDefault(c => c.Id == decision.TargetId);
                var stableTarget = selectedControl is null ? decision.TargetId
                    : $"{selectedControl.Role}:{selectedControl.AutomationId}:{selectedControl.Name}:{selectedControl.Parent}:{selectedControl.Bounds}";
                var actionKey = $"{decision.Operation}|{stableTarget}|{text}|{decision.Key}|{decision.Direction}";
                var stateKey = Fingerprint(snapshot);
                repeats = actionKey == lastAction && stateKey == lastState ? repeats + 1 : 0;
                lastAction = actionKey; lastState = stateKey;
                if (repeats >= 4 && decision.Operation is not ("wait" or "scroll"))
                    return Finish("needs_attention", "The same action repeatedly produced no screen change. Stopped to avoid a loop; inspect the target app and try again.");

                var targetLabel = snapshot.Controls.FirstOrDefault(x => x.Id == decision.TargetId)?.Name
                    ?? snapshot.Windows.FirstOrDefault(x => x.Id == decision.TargetId)?.Title
                    ?? snapshot.Apps.FirstOrDefault(x => x.Id == decision.TargetId)?.Name ?? decision.TargetId ?? "";
                Say($"Executing {decision.Operation}: {targetLabel}{(text is null ? "" : $" ← {text}")}");
                var attempt = ++operations;
                try
                {
                    var outcome = await worker.InvokeAsync(() => automation.Execute(decision, snapshot, text, ct), ct);
                    var step = new StepRecord(attempt, decision.Operation, targetLabel, text, outcome);
                    history.Add(step); Record("action", step);
                }
                catch (OperationCanceledException)
                {
                    var step = new StepRecord(attempt, decision.Operation, targetLabel, text,
                        "Cancelled during dispatch; input may have partially completed. Inspect the target before retrying.");
                    history.Add(step); Record("action_cancelled", step);
                    throw;
                }
                catch (Exception ex)
                {
                    // An input may have partly succeeded: reobserve, never blindly retry its old coordinates.
                    var step = new StepRecord(attempt, decision.Operation, targetLabel, text, "Action error; reobserve before retrying: " + ex.Message);
                    history.Add(step); Record("action_error", step); Say(step.Outcome);
                }
                await Task.Delay(options.SettleMilliseconds, ct);
            }
            // The final allowed action might itself complete the task. Observe it before reporting the limit.
            Say("Operation limit reached. Checking the result of the last action…");
            var finalCheck = await client.VerifyAsync(options.Goal, await Observe(), history, ct);
            Record("verification", finalCheck);
            return finalCheck.Achieved
                ? Finish("completed", $"Goal achieved after {operations} actions. {finalCheck.Evidence}")
                : Finish("limit_reached", $"Stopped at the {options.MaxOperations}-operation limit. Jev has not confirmed the goal.");
        }
        catch (OperationCanceledException) { return Finish("stopped", $"Stopped after {operations} actions."); }
        catch (Exception ex) { return Finish("error", ex.Message); }
    }

    private static string Fingerprint(Snapshot snapshot)
    {
        var content = snapshot.Window.Handle + "|" + string.Join("|", snapshot.Controls.Select(c => $"{c.Role}:{c.Name}:{c.Value}:{c.Enabled}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }
}
