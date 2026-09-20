using JetDesk;

var logDirectory = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "JetDesk-RunnerTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(logDirectory);

static void Assert(bool value, string name)
{
    if (!value) throw new InvalidOperationException("FAILED: " + name);
    Console.WriteLine("PASS: " + name);
}

async Task<RunResult> Run(WindowsAutomation desktop, JevClient classifier, int maximum = 50,
    CancellationToken token = default, int settleMilliseconds = 0)
{
    using var worker = new AutomationWorker();
    var runner = new AgentRunner(desktop, worker, classifier);
    return await runner.RunAsync(new("Exercise runner algorithm", maximum, 1, .25, settleMilliseconds), logDirectory, token);
}

{
    var desktop = new WindowsAutomation();
    var classifier = new JevClient();
    var result = await Run(desktop, classifier);
    Assert(result.Status == "limit_reached" && result.Operations == 50 && desktop.Inputs == 50 && classifier.Decisions == 50,
        "default 50-operation bound executes exactly 50 inputs and never a 51st");
    Assert(desktop.Observations == 51 && classifier.Verifications == 50,
        "each of 50 input outcomes gets a fresh observation and completion check before further input");
}
{
    var desktop = new WindowsAutomation();
    var classifier = new JevClient();
    var result = await Run(desktop, classifier, 1);
    Assert(result.Status == "limit_reached" && result.Operations == 1 && desktop.Inputs == 1, "user-configured lower operation maximum is honored");
}
{
    var desktop = new WindowsAutomation();
    var classifier = new JevClient { Verify = () => new(true, 1, "Synthetic achieved state after input") };
    var result = await Run(desktop, classifier, 1);
    Assert(result.Status == "completed" && result.Operations == 1 && desktop.Observations == 2,
        "completion caused by the final allowed input is recognized after reobservation");
}
{
    var desktop = new WindowsAutomation();
    var classifier = new JevClient
    {
        Decide = _ => Task.FromResult(new Decision("done", null, null, null, null, 1, true, "Synthetic premature completion")),
        Verify = () => new(false, 0, "Synthetic fresh-state rejection")
    };
    var result = await Run(desktop, classifier, 3);
    Assert(result.Status == "limit_reached" && result.Operations == 3 && desktop.Inputs == 0 && classifier.Decisions == 3,
        "rejected completion cannot spin forever or bypass the operation budget");
}
{
    var desktop = new WindowsAutomation();
    var classifier = new JevClient { Decide = _ => Task.FromResult(new Decision("invoke", "control", null, null, null, .01, false, "Synthetic uncertainty")) };
    var result = await Run(desktop, classifier, 3);
    Assert(result.Status == "limit_reached" && result.Operations == 3 && desktop.Inputs == 0,
        "low-confidence refreshes consume the loop budget without executing inputs");
}
{
    var desktop = new WindowsAutomation { ThrowOnInput = true };
    var result = await Run(desktop, new(), 2);
    Assert(result.Status == "limit_reached" && result.Operations == 2 && desktop.Inputs == 2 && desktop.Observations == 3,
        "failed input attempts consume the budget and require a new screen before retry");
}
{
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var desktop = new WindowsAutomation();
    var classifier = new JevClient();
    var result = await Run(desktop, classifier, token: cancellation.Token);
    Assert(result.Status == "stopped" && desktop.Inputs == 0 && classifier.Decisions == 0 && desktop.Observations == 0,
        "pre-cancellation stops before observation, classification, or input");
}
{
    using var cancellation = new CancellationTokenSource();
    var desktop = new WindowsAutomation();
    var classifier = new JevClient
    {
        Decide = async token =>
        {
            cancellation.CancelAfter(50);
            await Task.Delay(Timeout.Infinite, token);
            throw new Exception("Should never complete a canceled decision");
        }
    };
    var result = await Run(desktop, classifier, token: cancellation.Token);
    Assert(result.Status == "stopped" && desktop.Inputs == 0 && classifier.Decisions == 1,
        "cancellation during classification prevents the following input");
}
{
    using var cancellation = new CancellationTokenSource();
    var desktop = new WindowsAutomation { OnInput = cancellation.Cancel };
    var classifier = new JevClient();
    var result = await Run(desktop, classifier, token: cancellation.Token, settleMilliseconds: 5000);
    Assert(result.Status == "stopped" && result.Operations == 1 && desktop.Inputs == 1 && classifier.Decisions == 1,
        "cancellation after input interrupts settling and prevents another operation");
}
{
    var desktop = new WindowsAutomation { ChangeScreen = false, ChangeControlIds = true };
    var result = await Run(desktop, new());
    Assert(result.Status == "needs_attention" && desktop.Inputs == 4,
        "repeated identical action on unchanged screen stops even when observation-local control IDs change");
}
{
    var desktop = new WindowsAutomation();
    var classifier = new JevClient { Verify = () => new(desktop.Inputs > 0, 1, "Synthetic success after first input") };
    var result = await Run(desktop, classifier);
    Assert(result.Status == "completed" && result.Operations == 1 && desktop.Inputs == 1 &&
        classifier.Decisions == 1 && desktop.Observations == 2 && classifier.Verifications == 1,
        "fresh verified success stops after one input even when the next action classifier would repeat it");
}
{
    var desktop = new WindowsAutomation();
    var classifier = new JevClient { Verify = () => new(desktop.Inputs >= 2, desktop.Inputs >= 2 ? 1 : 0, "Synthetic success after second input") };
    var result = await Run(desktop, classifier);
    Assert(result.Status == "completed" && result.Operations == 2 && desktop.Inputs == 2 &&
        classifier.Decisions == 2 && desktop.Observations == 3 && classifier.Verifications == 2,
        "negative post-input verification permits the next decision and the first positive verification ends the loop");
}
{
    using var cancellation = new CancellationTokenSource();
    var desktop = new WindowsAutomation();
    var classifier = new JevClient
    {
        VerifyWithCancellation = async token =>
        {
            cancellation.CancelAfter(50);
            await Task.Delay(Timeout.Infinite, token);
            throw new Exception("Should not finish a canceled outcome check");
        }
    };
    var result = await Run(desktop, classifier, token: cancellation.Token);
    Assert(result.Status == "stopped" && result.Operations == 1 && desktop.Inputs == 1 &&
        classifier.Decisions == 1 && classifier.Verifications == 1,
        "cancellation during post-input verification prevents the next action decision and input");
}

Console.WriteLine("Runner algorithm suite passed. Desktop and classifier adapters were fake; production AgentRunner source was exercised.");
Console.WriteLine("Synthetic test trace directory: " + logDirectory);
