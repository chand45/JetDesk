# JetDesk verification

## JetDesk rename checks

On 20 September 2026, `Build.ps1` published `app/JetDesk.exe`, the renamed native fixture built without warnings or errors, and the deterministic client and runner suites passed. Launching through `Start JetDesk.cmd` opened a native window whose title and heading were both **JetDesk**, with **Run request** enabled. The executable's product metadata and CLI usage also identify it as JetDesk. These checks cover the rename; the live API and Spotify scenarios below were not rerun for this branding change.

## Historical functional verification

Verified on Windows on 20 September 2026 using the installed .NET 8 Desktop Runtime and the real Jev API. This report separates native application behavior, classifier tests, and simulated test adapters. The published executable and assembly hashes are recorded in [evidence/verification-summary.json](evidence/verification-summary.json).

These runs predate the rename from Jev Desktop to JetDesk. Their evidence, executable names, and hashes are preserved as historical records; they do not identify the rebuilt JetDesk executable. Jev remains the classifier provider used by JetDesk.

**The final published GUI completed `play sweater weather on spotify` in five actions**, starting from a different song and search query. It opened Spotify, entered `sweater weather`, pressed Enter, opened the matching result, and pressed Play. It then verified the outcome and stopped. Spotify visibly showed Sweater Weather by The Neighbourhood, Pause, and progress at `0:11`; the Jev window reported completion and restored its controls.

## Observed results

| Check | Result | What the evidence establishes |
|---|---|---|
| Final published GUI, full Spotify search | Completed after **5 actions** | Automatic target/default 50; actual search text entry, Enter, result navigation, and playback. Six observations, five decisions, and five post-action verifications. The first four checks were negative; the final check was positive. |
| Controlled native music window, real Jev | Completed after **4 actions** | Entered the song title, searched, selected the original recording, and pressed Play. Six UI observations and five Jev decisions were recorded. An independent event log confirmed the sequence and an advancing playback timer. |
| Actual one-operation limit | Stopped at **1 operation** | Entered the search text, observed the resulting screen, and returned `limit_reached` after Jev declined completion. No second input was executed. |
| Earlier Spotify completion correction | Completed after **1 action** | With another track playing and the requested song already visible, invoked its Play button. Subsequent observations showed `Now playing: Sweater Weather by The Neighbourhood`, the global Pause control, and progress advancing from `0:00` to `0:03`. |
| Earlier GUI request | Completed after **2 actions** | Typed the original request into the GUI with Automatic/default 50. Jev opened Spotify and invoked the requested song's already visible Play control. Four observations and three decisions preceded completion. |
| Deterministic client tests, final source | **96 assertions passed** | Production request/response logic, offered choices, candidate text, privacy exclusions, bounds, cancellation, errors, completion rules, and context-limit retries; fake HTTP responses. |
| Real Jev classifier checks | **5 assertions passed** | Synthetic text snapshots: active correct track accepted; search-only, paused, wrong-track, and incomplete mixed-goal states rejected. These checks performed no desktop input. |
| Runner algorithm tests, final source | **14 assertions passed** across 13 scenarios | Production runner source, fake desktop/classifier adapters: default 50-operation bound, lower limits, verification before another input, completion checks, error budgets, cancellation, and an unchanged-screen loop with changing observation IDs. |
| Published GUI cancellation | **F8 and Stop passed** | Both runs stopped after one operation, ended their traces with `stopped`, and restored the editable request and enabled Run button. |

The controlled music window simulates playback. Spotify runs used the installed Spotify application and its observed playback state; sound output was not recorded or measured. The one-action and two-action runs did not repeat search. The final five-action run did.

## Completion verification

The original Spotify attempt reached the requested playback state after its first four inputs, but the earlier completion check repeatedly returned probabilities between 0.76 and 0.82, below the unchanged 0.85 threshold. The run continued, made one further title click, and ended with an API HTTP 400 error after 15 counted operations. That attempt is recorded as a failure, not a completed run.

The corrected client asks Jev whether the entire goal is solely playback. For such goals it checks the current requested item, active playback, and requested application separately against observed player controls. Every check must reach 0.85 and Jev must select observed evidence. Mixed requests retain verification of the entire goal. In the final full-search run, those probabilities were **0.89, 0.92, and 0.97**; the final minimum was 0.89. The evidence verifier confirmed that this judgment followed a fresh screen observation and that no further input followed it. The earlier one-action run returned 0.92, 0.95, and 0.97.

Real API checks on synthetic states against the final compact/retry client independently returned: correct active playback 0.94; search-only 0.19; paused 0.47; wrong track 0.05; and playback plus an unproven volume-setting request 0.07. These values describe those particular API responses, not a general accuracy benchmark. The earlier 88-assertion client run and its five live checks are retained separately in the evidence summary; the current suite adds eight retry regressions.

## GUI acceptance

The controlling agent operated the published GUI and visually confirmed both cancellation paths. **F8** stopped a run at 13:11:15 India time; the **Stop button** stopped another at 13:11:28. Both results reported one operation. Independent inspection confirmed each trace contains one terminal `stopped` result as its final record, with no later action. The request became editable, Run became enabled, and Stop became disabled.

A separate fresh GUI Spotify request exposed an HTTP 400 error after launching Spotify and a second operation. Read-only replay identified the provider's `max_tokens_exceeded` response. The final client compacts redundant request data and retries that specific condition with successively smaller subsets of the same observation. Tests confirm the retry rebuilds its offered choices, rejects IDs that were removed, stops after a bounded number of attempts, and does not broadly retry unrelated HTTP 400 errors. It executes no desktop input until a complete valid decision is available.

After that correction was published, entering the original Spotify request into the GUI completed after two actions: open Spotify, then Play the requested visible song. The trace independently confirms Automatic selection, the default 50-operation maximum, a fresh completion check, and a final probability of 0.92.

A further GUI request for a different song performed text entry, search, and playback, but the action classifier continued selecting Play instead of proposing completion. That exploratory run was stopped with F8 after 15 counted operations and is not a completion pass. The final runner checks each fresh post-input screen with the independent verifier before asking for another action. Fourteen runner assertions now cover this behavior, including immediate stopping when an achieved outcome is verified, continued progress after negative verification, and cancellation during verification. The earlier 11-assertion result is retained as historical evidence.

The final published GUI was then tested with the original request while Spotify still showed the different song and query. The full search-and-play flow completed at **13:20:15 India time** after five actions. Independent trace verification passed. Rendered-window inspection confirmed Spotify playback, the completed Jev status, editable request, enabled Run, and disabled Stop. The long completion status stayed within the form with an ellipsis rather than stretching the layout. The cancellation controls are unchanged from the build in which F8 and Stop were exercised.

## Scope and limits

- The tested screen extractor is Windows UI Automation. Screenshot OCR, inaccessible canvases, games, and remote-desktop image surfaces are not covered.
- Tests support the workflows above; they do not establish reliability across every Windows application, arbitrary request, screen size, or account state.
- The maximum counts loop operations and attempts, including rejected completion checks and uncertain refreshes. A cancelled input attempt is counted and logged because input may have partially completed.
- The runner tests verify 50-step behavior with test adapters. The live lower-limit test verifies the same production loop against the native fixture and real service.
- Password controls are excluded from classifier state and candidates. Key values were checked against the actual run traces and were absent. Full live UI traces can contain unrelated visible application data, so the distributed evidence includes only allowlisted summaries and source hashes.

## Reproduce

From this application folder, with the .NET SDK installed:

```powershell
dotnet run --project tests/client/ClientTests.csproj -c Release
dotnet run --project tests/runner/RunnerTests.csproj -c Release
dotnet run --project tests/client/ClientTests.csproj -c Release -- --live
```

The final command uses `JEV_KEY` and calls the real service with synthetic snapshots. For the native fixture and actual trace verifier, see [tests/verification-plan.md](tests/verification-plan.md). New runs write their own evidence; the summary supplied here is a dated record of the inspected runs.
