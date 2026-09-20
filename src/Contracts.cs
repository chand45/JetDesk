namespace JetDesk;

public sealed record ScreenRect(int X, int Y, int Width, int Height);
public sealed record WindowInfo(string Id, string Title, string ProcessName, long Handle);
public sealed record AppInfo(string Id, string Name, string LaunchId);

public sealed record ControlInfo(
    string Id, string Role, string Name, string Value, string Parent,
    string AutomationId, ScreenRect Bounds, List<string> Actions,
    bool Enabled, bool IsPassword);

public sealed record Snapshot(
    WindowInfo Window, List<ControlInfo> Controls, List<WindowInfo> Windows,
    List<AppInfo> Apps, string? FocusedId, bool Truncated)
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record TextCandidate(string Id, string Text, string Source);
public sealed record StepRecord(int Step, string Operation, string Target, string? Text, string Outcome);
public sealed record Decision(
    string Operation, string? TargetId, string? TextId, string? Key,
    string? Direction, double Confidence, bool GoalAchieved, string? Evidence)
{
    public string Model { get; init; } = "";
}
public sealed record CompletionCheck(bool Achieved, double Probability, string Evidence);

public sealed record RunOptions(string Goal, int MaxOperations = 50,
    long? TargetHandle = null, double MinimumConfidence = 0.25, int SettleMilliseconds = 700);
public sealed record RunResult(string Status, int Operations, string Message, string LogPath);
