# JetDesk verification plan

The real provider + native UI workflow is the main integration check. Component tests cover boundaries and failure handling; those tests do not by themselves prove desktop automation or Jev inference works.

## Deterministic client tests

From the delivered application folder:

```powershell
dotnet run --project tests/client/ClientTests.csproj -c Release
```

This no-package test executable links the production client, contracts, and text candidate builder. Its fake HTTP transport checks response validation, candidate limits and text preservation, password exclusion, cancellation, credentials and redacted errors, retry handling, and response-size bounds. The tests do not read the real key or operate the desktop. Appending `-- --live` additionally calls the real Jev service using `JEV_KEY` against explicitly synthetic screen states; that optional test validates classification and does not claim real desktop playback.

## Runner algorithm tests

```powershell
dotnet run --project tests/runner/RunnerTests.csproj -c Release
```

This harness compiles the production `AgentRunner.cs` against deliberately fake desktop and classifier adapters. It verifies the actual loop's default 50-operation maximum, a lower configured maximum, reobservation, final-action completion, rejection of premature completion, failed-input and low-confidence budgets, cancellation boundaries, and stopping an unchanged-state loop. It writes clearly synthetic traces into a temporary directory (or a supplied first argument). These tests never interact with the desktop or call Jev.

## Native Windows integration fixture

The independent fixture source lives in `tests/fixture` within the delivered application folder. It is a WinForms application named **JetDesk Test Music**. The original build used for task verification was named **Jev Test Music** and also lives in `work/fixture` in the original task workspace. The fixture offers real native controls discovered by UI Automation:

1. Initially only a search input and Search action are available.
2. Searching a title populates matching song results.
3. Selecting a track reveals Play.
4. Play changes the visible state to `Now playing: ...`, changes the button to Pause, and advances an elapsed timer.

The fixture contains both the original recording and a cover to test candidate selection. No Jev response or application action is injected into the fixture. The fixture writes timestamped actual UI events to the `--events` path and PID/HWND to `--ready-file`.

From the delivered application folder, build the fixture with `dotnet build tests/fixture/JetDesk.TestMusic.csproj -c Release`. The supervising test should launch it visibly, pass its HWND to JetDesk, request `Play Sweater Weather by The Neighbourhood`, and use the **real JEV_KEY and provider**. Success requires the UI fixture to record search, track selection, and play in that order; the actual runner must return completed based on a fresh observed state. Inspect the trace for a different control set after each state change. The fixture simulates playback; it does not prove Spotify audio playback.

Run the read-only evidence verifier after the run. It checks the actual trace, terminal result, and independent UI event log; it does not drive the fixture or manufacture model responses:

```powershell
./tests/Verify-Run.ps1 -ResultPath ./run-result.json -ExpectedMaximum 50 -FixtureEventsPath ./fixture-events.jsonl -ReportPath ./verification.json
```

For a deliberate lower-limit run, use `-ExpectedStatus limit_reached -ExpectedMaximum 1` and omit `-FixtureEventsPath`. For cancellation use `-ExpectedStatus stopped`. These checks validate the supplied evidence; the verifier does not claim that all available boundary cases were exercised.

## Required boundary checks

- A configured maximum of 50 operations never executes operation 51, including retries and focus operations if counted as runner steps.
- An explicit lower maximum stops the loop at that maximum and returns an iteration-limit status rather than completion.
- Cancellation before and during provider requests or settle delays stops promptly and causes no subsequent operation.
- `JEV_KEY` resolves from permitted Windows environment scopes without writing its value to logs, command lines, or snapshots.
- A missing key produces an actionable error before automation begins; invalid/rejected credentials surface a redacted provider error.
- A selected control removed or materially changed since the last snapshot is rejected or re-observed, not blindly clicked at an old coordinate.
- Password controls never expose values as state or candidates, and the controller refuses entering a candidate into a password field.
- The provider is offered a `none`/unsupported option and cannot select arbitrary executable code, URLs, or text outside candidates.
- Empty or unsupported accessibility trees produce a bounded, truthful outcome rather than false completion.
- Completion requires a fresh screen snapshot and a positive Jev assessment; confidence or an executed action alone is not evidence of success.

## User-facing application check

Launch **app/JetDesk.exe** (or **Start JetDesk.cmd**) and verify a user can enter a request, choose a target application/window, set the operation limit (default 50), start, read progress, and cancel. Validate the requested Spotify example against the actual installed Spotify/web app separately from fixture coverage, stating any account or app availability limitation.
