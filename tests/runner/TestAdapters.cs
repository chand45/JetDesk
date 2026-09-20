namespace JevDesktop;

// Algorithm-only adapters. These deliberately do not link the production desktop or network code.
// AgentRunner itself is compiled directly from production source by the test project.
public sealed class WindowsAutomation
{
    public long? TargetHandle { get; set; }
    public int Observations { get; private set; }
    public int Inputs { get; private set; }
    public Action? OnInput { get; set; }
    public bool ChangeScreen { get; set; } = true;
    public bool ChangeControlIds { get; set; }
    public bool ThrowOnInput { get; set; }
    public Snapshot Observe()
    {
        Observations++;
        return new(new("fixture", "Synthetic fixture state", "Test", 1),
            [new(ChangeControlIds ? "control" + Observations : "control", "Button", "Test control", ChangeScreen ? Observations.ToString() : "constant",
                "Fixture", "control", new(0, 0, 100, 50), ["invoke"], true, false)], [], [], null, false);
    }
    public string Execute(Decision decision, Snapshot snapshot, string? text, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Inputs++;
        OnInput?.Invoke();
        if (ThrowOnInput) throw new InvalidOperationException("Synthetic input failure");
        return "Synthetic adapter input recorded; no desktop operation occurred.";
    }
}

public sealed class JevClient
{
    public int Decisions { get; private set; }
    public int Verifications { get; private set; }
    public Func<CancellationToken, Task<Decision>>? Decide { get; set; }
    public Func<CompletionCheck>? Verify { get; set; }
    public Func<CancellationToken, Task<CompletionCheck>>? VerifyWithCancellation { get; set; }
    public async Task<Decision> DecideAsync(string goal, Snapshot snapshot, IReadOnlyList<StepRecord> history,
        IReadOnlyList<TextCandidate> candidates, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Decisions++;
        return Decide is null ? new("invoke", snapshot.Controls[0].Id, null, null, null, 1, false, "Synthetic choice") : await Decide(cancellation);
    }
    public Task<CompletionCheck> VerifyAsync(string goal, Snapshot snapshot, IReadOnlyList<StepRecord> history, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Verifications++;
        return VerifyWithCancellation?.Invoke(cancellation) ?? Task.FromResult(Verify?.Invoke() ?? new(false, 0, "Synthetic incomplete state"));
    }
}
