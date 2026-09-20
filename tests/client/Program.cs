using JetDesk;
using System.Net;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

static void Assert(bool value, string label)
{
    if (!value) throw new Exception("FAILED: " + label);
    Console.WriteLine("PASS: " + label);
}

var field = new ControlInfo("e1", "Edit", "Search Spotify", "", "Main toolbar", "search", new(10, 10, 200, 30), ["set_text"], true, false);
var snapshot = new Snapshot(new("w1", "Spotify", "Spotify", 1), [field], [], [], "e1", false);
var candidates = TextCandidates.Build("play sweater weather on spotify", snapshot);
Assert(candidates.Any(c => c.Text == "sweater weather"), "deterministic multiword song extraction");
var quoted = TextCandidates.Build("type \"Hello, world!\" in the box", snapshot);
Assert(quoted[0].Text == "Hello, world!", "quoted literal retains punctuation");
var privateSnapshot = snapshot with { Controls = [field with { Name = "do not expose", IsPassword = true }] };
Assert(!TextCandidates.Build("search something", privateSnapshot).Any(c => c.Text.Contains("do not expose")), "password controls excluded from candidates");
Assert(TextCandidates.Build(string.Join(" ", Enumerable.Range(0, 100).Select(i => "word" + i)), snapshot).Count <= 180, "text choice count bounded");
var printer = snapshot with { Controls = [field with { Role = "Text", Name = "HP LaserJet Pro M404dn", Actions = [] }] };
Assert(TextCandidates.Build("Find the manual for this printer", printer).Any(c => c.Text == "HP LaserJet Pro M404dn manual"), "screen entity plus fixed template constructs search text");
Assert(TextCandidates.Build("", snapshot).Count == 0, "empty requests produce no text candidates");

var missingKeyRejected = false;
try { using var ignored = new JevClient(" "); }
catch (InvalidOperationException ex) { missingKeyRejected = ex.Message.Contains("JEV_KEY"); }
Assert(missingKeyRejected, "explicitly missing key produces actionable error without reading or altering stored key");

var crowded = snapshot with
{
    Controls = Enumerable.Range(0, 180).Select(i => field with { Id = "e" + i, Name = "Control " + i, Actions = ["set_text", "invoke", "select", "toggle", "expand", "scroll"] }).ToList(),
    Windows = Enumerable.Range(0, 15).Select(i => new WindowInfo("w" + (i + 100), "Window " + i, "App", 100 + i)).ToList(),
    Apps = Enumerable.Range(0, 15).Select(i => new AppInfo("app" + i, "Application " + i, "launch" + i)).ToList()
};
using (var client = new JevClient("fake-test-key", new Handler(async request =>
{
    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
    var choices = body.RootElement.GetProperty("questions").GetProperty("action").GetProperty("criteria");
    Assert(choices.EnumerateObject().Count() == 255, "crowded snapshot uses exactly 255 choices");
    var descriptions = choices.EnumerateObject().Select(p => p.Value.GetString()!).ToList();
    foreach (var key in new[] { "Enter", "Tab", "Escape", "Ctrl+L", "Ctrl+F", "Up", "Down", "Left", "Right", "Space", "Backspace", "Home", "End", "PageUp", "PageDown", "Shift+Tab" })
        Assert(descriptions.Any(s => s.StartsWith("Press " + key + " ")), "crowded snapshot retains " + key);
    Assert(descriptions.Any(s => s.StartsWith("Wait briefly")) && descriptions.Any(s => s.StartsWith("Finish:")) && descriptions.Any(s => s.StartsWith("Stop:")) && choices.TryGetProperty("none", out _), "crowded snapshot retains all terminal/wait options");
    return Reply(new { action = new { choice = "none", confidence = .9 }, completed = new { noul = 0 }, evidence = new { choice = "none", confidence = .9 } });
})))
    await client.DecideAsync("play sweater weather", crowded, [], candidates, default);

var call = 0;
using (var client = new JevClient("fake-test-key", new Handler(async request =>
{
    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
    var questions = body.RootElement.GetProperty("questions");
    call++;
    if (call == 1)
    {
        var choices = questions.GetProperty("action").GetProperty("criteria");
        Assert(choices.EnumerateObject().Count() <= 255, "action choice count bounded");
        var action = choices.EnumerateObject().Single(p => p.Value.GetString()!.StartsWith("set_text:")).Name;
        return Reply(new { action = new { choice = action, confidence = .9 }, completed = new { noul = .02 }, evidence = new { choice = "none", confidence = .9 } });
    }
    var text = questions.GetProperty("text").GetProperty("criteria").EnumerateObject()
        .First(p => p.Value.GetString()!.StartsWith("sweater weather [")).Name;
    return Reply(new { text = new { choice = text, confidence = .8 } });
})))
{
    var result = await client.DecideAsync("play sweater weather on spotify", snapshot, [], candidates, default);
    Assert(result.Operation == "set_text" && result.TargetId == "e1", "compound action preserves operation and target");
    Assert(candidates.Single(c => c.Id == result.TextId).Text == "sweater weather", "text choice copies offered candidate");
    Assert(result.Confidence == .8 && client.ApiCalls == 2, "dependent text choice confidence/call accounting");
}

using (var client = new JevClient("fake-test-key", new Handler(_ => Task.FromResult(Reply(new
{
    action = new { choice = "invented-id", confidence = 1 }, completed = new { noul = 0 }, evidence = new { choice = "none", confidence = 1 }
})))))
{
    var rejected = false;
    try { await client.DecideAsync("play sweater weather", snapshot, [], candidates, default); }
    catch (InvalidOperationException) { rejected = true; }
    Assert(rejected, "unoffered model action rejected");
}

using (var client = new JevClient("fake-test-key", new Handler(_ => Task.FromResult(Reply(new
{
    completed = new { noul = .99 }, evidence = new { choice = "none", confidence = 1 }
})))))
{
    var result = await client.VerifyAsync("play sweater weather", snapshot, [], default);
    Assert(!result.Achieved, "completion requires selected observed evidence");
}

foreach (var number in new[] { "1.01", "-0.01", "1e999", "\"0.9\"", "null" })
{
    using var client = new JevClient("fake-test-key", new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"answers\":{\"completed\":{\"noul\":" + number + "},\"evidence\":{\"choice\":\"none\",\"confidence\":1}}}")
    })));
    var rejected = false;
    try { await client.VerifyAsync("play sweater weather", snapshot, [], default); }
    catch (InvalidOperationException) { rejected = true; }
    Assert(rejected, "invalid/out-of-range completion probability is rejected: " + number);
}

using (var canceled = new CancellationTokenSource())
using (var client = new JevClient("fake-test-key", new Handler(_ => throw new Exception("Must not send a canceled request"))))
{
    canceled.Cancel();
    var stopped = false;
    try { await client.VerifyAsync("play sweater weather", snapshot, [], canceled.Token); }
    catch (OperationCanceledException) { stopped = true; }
    Assert(stopped && client.ApiCalls == 0, "pre-cancellation prevents any API request");
}

using (var cancellation = new CancellationTokenSource())
using (var client = new JevClient("fake-test-key", new Handler(async (_, ct) =>
{
    await Task.Delay(Timeout.Infinite, ct);
    throw new Exception("Must not complete a canceled request");
})))
{
    var clock = Stopwatch.StartNew();
    cancellation.CancelAfter(75);
    var stopped = false;
    try { await client.VerifyAsync("play sweater weather", snapshot, [], cancellation.Token); }
    catch (OperationCanceledException) { stopped = true; }
    Assert(stopped && clock.Elapsed < TimeSpan.FromSeconds(3), "in-flight request cancellation stops promptly");
}

const string fakeSecret = "fake-secret-sentinel-do-not-log";
using (var client = new JevClient(fakeSecret, new Handler(async request =>
{
    Assert(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization?.Parameter == fakeSecret,
        "API uses Bearer authorization with the supplied key");
    Assert(!(await request.Content!.ReadAsStringAsync()).Contains(fakeSecret), "request state omits the API key");
    return new(HttpStatusCode.Unauthorized) { Content = new StringContent("Untrusted service response: " + fakeSecret) };
})))
{
    var redacted = false;
    try { await client.VerifyAsync("play sweater weather", snapshot, [], default); }
    catch (HttpRequestException ex) { redacted = ex.StatusCode == HttpStatusCode.Unauthorized && !ex.Message.Contains(fakeSecret); }
    Assert(redacted, "rejected credentials produce a redacted actionable error");
}

var retryCalls = 0;
using (var client = new JevClient("fake-test-key", new Handler(_ =>
{
    if (++retryCalls == 1)
    {
        var retry = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        retry.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        return Task.FromResult(retry);
    }
    return Task.FromResult(Reply(new { completed = new { noul = .01 }, evidence = new { choice = "none", confidence = .99 } }));
})))
{
    var result = await client.VerifyAsync("play sweater weather", snapshot, [], default);
    Assert(retryCalls == 2 && client.ApiCalls == 3 && !result.Achieved, "transient rate limit retries once after goal-mode classification and uses the actual returned answer");
}

using (var client = new JevClient("fake-test-key", new Handler(async request =>
{
    var raw = await request.Content!.ReadAsStringAsync();
    Assert(!raw.Contains("private-field-sentinel") && !raw.Contains("private-value-sentinel"), "password names and values are excluded from classifier state and choices");
    return Reply(new { completed = new { noul = .01 }, evidence = new { choice = "none", confidence = 1 } });
})))
{
    await client.VerifyAsync("search something", snapshot with { Controls = [field with { Name = "private-field-sentinel", Value = "private-value-sentinel", IsPassword = true }] }, [], default);
}

using (var client = new JevClient("fake-test-key", new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent(new string(' ', 2_000_001))
}))))
{
    var bounded = false;
    try { await client.VerifyAsync("play sweater weather", snapshot, [], default); }
    catch (InvalidOperationException ex) { bounded = ex.Message.Contains("exceeded"); }
    Assert(bounded, "oversized provider response is bounded before parsing");
}

{
    var sizes = new List<int>();
    var times = new List<string>();
    var actionIds = new List<string>();
    using var client = new JevClient("fake-test-key", new Handler(async request =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        var current = body.RootElement.GetProperty("state").GetProperty("currentScreen");
        sizes.Add(current.GetProperty("controls").GetArrayLength());
        times.Add(current.GetProperty("CapturedAt").GetString()!);
        var choices = body.RootElement.GetProperty("questions").GetProperty("action").GetProperty("criteria");
        var action = choices.EnumerateObject().First(p => p.Value.GetString()!.StartsWith("invoke:") && p.Value.GetString()!.Contains("e1 [")).Name;
        actionIds.Add(action);
        return sizes.Count == 1 ? ContextExceeded() : Reply(new
        {
            action = new { choice = action, confidence = .99 }, completed = new { noul = .01 }, evidence = new { choice = "none", confidence = 1 }
        });
    }));
    var choice = await client.DecideAsync("play sweater weather", crowded, [], candidates, default);
    Assert(sizes.SequenceEqual(new[] { 180, 120 }) && times.Distinct().Count() == 1,
        "explicit context-limit error retries a smaller subset of the same captured screen");
    Assert(actionIds[0] != actionIds[1] && choice.Operation == "invoke" && choice.TargetId == "e1",
        "context retry rebuilds the offered action dictionary and preserves the newly selected target");
}
{
    var simpleCrowded = crowded with { Windows = [], Apps = [], Controls = crowded.Controls.Select(c => c with { Actions = ["invoke"] }).ToList() };
    var calls = 0;
    string? removedChoice = null;
    using var client = new JevClient("fake-test-key", new Handler(async request =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        var choices = body.RootElement.GetProperty("questions").GetProperty("action").GetProperty("criteria");
        if (++calls == 1)
        {
            removedChoice = choices.EnumerateObject().Last(p => p.Value.GetString()!.StartsWith("invoke:")).Name;
            return ContextExceeded();
        }
        Assert(!choices.TryGetProperty(removedChoice!, out _), "context reduction removes action IDs that are no longer offered");
        return Reply(new { action = new { choice = removedChoice, confidence = 1 }, completed = new { noul = 0 }, evidence = new { choice = "none", confidence = 1 } });
    }));
    var rejected = false;
    try { await client.DecideAsync("play sweater weather", simpleCrowded, [], candidates, default); }
    catch (InvalidOperationException ex) { rejected = ex.Message.Contains("not offered"); }
    Assert(rejected && calls == 2, "a response selecting a removed pre-retry ID is rejected without returning an action");
}
{
    var calls = 0;
    using var client = new JevClient("fake-test-key", new Handler(_ =>
    {
        calls++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        { Content = new StringContent("{\"detail\":{\"error_type\":\"invalid_request\"}}") });
    }));
    var failed = false;
    try { await client.DecideAsync("play sweater weather", crowded, [], candidates, default); }
    catch (HttpRequestException ex) { failed = ex.StatusCode == HttpStatusCode.BadRequest; }
    Assert(failed && calls == 1, "unrelated HTTP 400 errors are not retried as context failures");
}
{
    var sizes = new List<int>();
    using var client = new JevClient("fake-test-key", new Handler(async request =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        sizes.Add(body.RootElement.GetProperty("state").GetProperty("currentScreen").GetProperty("controls").GetArrayLength());
        return ContextExceeded();
    }));
    var failed = false;
    try { await client.DecideAsync("play sweater weather", crowded, [], candidates, default); }
    catch (InvalidOperationException ex) { failed = ex.Message.Contains("context limit"); }
    Assert(failed && sizes.SequenceEqual(new[] { 180, 120, 80, 48, 24 }),
        "persistent context-limit failures stop after the bounded shrinking schedule");
}
{
    var sizes = new List<int>();
    using var client = new JevClient("fake-test-key", new Handler(async request =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        sizes.Add(body.RootElement.GetProperty("state").GetProperty("currentScreen").GetProperty("controls").GetArrayLength());
        return sizes.Count == 1 ? ContextExceeded() : Reply(new { completed = new { noul = .01 }, evidence = new { choice = "none", confidence = 1 } });
    }));
    var check = await client.VerifyAsync("play sweater weather and set volume to 20%", crowded, [], default);
    Assert(!check.Achieved && sizes.SequenceEqual(new[] { 180, 120 }) && client.ApiCalls == 3,
        "completion verification also shrinks context while reusing the already classified goal mode");
}
{
    var richCandidates = TextCandidates.Build("play \"sweater weather\" on spotify " + string.Join(" ", Enumerable.Range(0, 100).Select(i => "word" + i)), crowded);
    var textSizes = new List<int>();
    var actionCalls = 0;
    using var client = new JevClient("fake-test-key", new Handler(async request =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        var questions = body.RootElement.GetProperty("questions");
        if (questions.TryGetProperty("action", out var actionQuestion))
        {
            actionCalls++;
            var chosen = actionQuestion.GetProperty("criteria").EnumerateObject().First(p => p.Value.GetString()!.StartsWith("set_text:") && p.Value.GetString()!.Contains("e1 [")).Name;
            return Reply(new { action = new { choice = chosen, confidence = .99 }, completed = new { noul = .01 }, evidence = new { choice = "none", confidence = 1 } });
        }
        var text = questions.GetProperty("text").GetProperty("criteria");
        textSizes.Add(text.EnumerateObject().Count());
        if (textSizes.Count == 1) return ContextExceeded();
        var chosenText = text.EnumerateObject().First(p => p.Value.GetString()!.StartsWith("sweater weather [")).Name;
        return Reply(new { text = new { choice = chosenText, confidence = .98 } });
    }));
    var choice = await client.DecideAsync("play sweater weather on spotify", crowded, [], richCandidates, default);
    Assert(actionCalls == 2 && textSizes.SequenceEqual(new[] { 181, 121 }) && choice.Operation == "set_text" &&
        richCandidates.Single(c => c.Id == choice.TextId).Text == "sweater weather",
        "text-context failure rebuilds the decision with fewer offered literals before returning exact selected text");
}

var playbackTrack = new ControlInfo("p1", "Text", "Sweater Weather", "", "Now playing", "track", new(10, 80, 200, 30), [], true, false);
var playbackPause = new ControlInfo("p2", "Button", "Pause", "", "Player controls", "pause", new(210, 80, 50, 30), ["invoke"], true, false);
var searchDistractor = new ControlInfo("r1", "Text", "Sweater Weather (Cover)", "", "Search results", "result", new(10, 200, 200, 30), [], true, false);
var albumPlay = new ControlInfo("r2", "Button", "Play Sweater Weather", "", "Album result row", "albumPlay", new(200, 200, 50, 30), ["invoke"], true, false);
var playbackScreen = snapshot with { Controls = [playbackTrack, playbackPause, searchDistractor, albumPlay] };

async Task<CompletionCheck> PlaybackCheck(Snapshot screen, double titleProbability, double playingProbability,
    double appProbability, string evidenceId, Action<JsonElement>? inspect = null)
{
    var modeCalls = 0;
    using var client = new JevClient("fake-test-key", new Handler(async request =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        var questions = body.RootElement.GetProperty("questions");
        if (questions.TryGetProperty("mode", out _))
        {
            modeCalls++;
            Assert(body.RootElement.GetProperty("state").EnumerateObject().Count() == 1 &&
                body.RootElement.GetProperty("state").TryGetProperty("userGoal", out _),
                "verification mode classifies only the complete user goal");
            return Reply(new { mode = new { choice = "playback", confidence = 1 } });
        }
        Assert(questions.TryGetProperty("title_matches", out _) && questions.TryGetProperty("playing", out _) &&
            questions.TryGetProperty("app_matches", out _) && !questions.TryGetProperty("completed", out _),
            "playback verification asks separate current-title, active-player, and requested-app questions");
        inspect?.Invoke(body.RootElement.GetProperty("state"));
        return Reply(new
        {
            title_matches = new { noul = titleProbability }, playing = new { noul = playingProbability },
            app_matches = new { noul = appProbability }, evidence = new { choice = evidenceId, confidence = 1 }
        });
    }, autoMode: false));
    var check = await client.VerifyAsync("play sweater weather on spotify", screen, [], default);
    Assert(modeCalls == 1 && client.ApiCalls == 2, "playback procedure classifies the goal then evaluates the observed player state");
    return check;
}

var correctPlaying = await PlaybackCheck(playbackScreen, .97, .96, .99, "p2", state =>
{
    var controls = state.GetProperty("controls").EnumerateArray().ToList();
    Assert(controls.Any(c => c.GetProperty("id").GetString() == "p1") && controls.Any(c => c.GetProperty("id").GetString() == "p2"),
        "player projection preserves both current track and transport controls");
    Assert(!controls.Any(c => c.GetProperty("id").GetString() == "r1"), "player projection excludes a matching search-result distractor");
    Assert(!controls.Any(c => c.GetProperty("id").GetString() == "r2"), "named global player region excludes duplicate album-row Play controls");
});
Assert(correctPlaying.Achieved && correctPlaying.Probability == .96, "playback completion requires all three checks and reports the weakest probability");

var nativeFixtureScreen = snapshot with { Controls = [
    playbackTrack with { Name = "Now playing: Sweater Weather - The Neighbourhood", Parent = "Window: JetDesk Test Music" },
    playbackTrack with { Id = "p3", Name = "Playing: Sweater Weather - The Neighbourhood", Parent = "Window: JetDesk Test Music" },
    playbackPause with { Name = "Pause playback", Parent = "Window: JetDesk Test Music" }
] };
Assert((await PlaybackCheck(nativeFixtureScreen, .99, .99, .99, "p2", state =>
{
    Assert(state.GetProperty("controls").EnumerateArray().Any(c => c.GetProperty("name").GetString() == "Pause playback"),
        "native fixture Pause playback control survives alongside multiple player status labels");
})).Achieved, "transport labels with a playback suffix remain available for verification");

var pausedScreen = playbackScreen with { Controls = [playbackTrack, playbackPause with { Name = "Play" }] };
Assert(!(await PlaybackCheck(pausedScreen, .99, .02, .99, "p2")).Achieved, "matching current track while paused does not complete playback");
var wrongTrackScreen = playbackScreen with { Controls = [playbackTrack with { Name = "Softcore" }, playbackPause, searchDistractor with { Name = "Sweater Weather" }] };
Assert(!(await PlaybackCheck(wrongTrackScreen, .02, .99, .99, "p2", state =>
{
    var raw = state.GetProperty("controls").GetRawText();
    Assert(raw.Contains("Softcore") && !raw.Contains("Sweater Weather"), "matching search result cannot replace a different current track in the player projection");
})).Achieved, "actively playing the wrong track does not complete the request");
var searchOnlyScreen = snapshot with { Controls = [searchDistractor with { Name = "Sweater Weather" }, playbackPause with { Name = "Play", Parent = "Search results" }] };
Assert(!(await PlaybackCheck(searchOnlyScreen, .04, .02, .99, "none", state =>
{
    Assert(state.GetProperty("controls").EnumerateArray().All(c => c.GetProperty("parent").GetString() == "Search results"),
        "fallback projection retains search-result context without inventing a current-item region");
})).Achieved, "search results and a result-row Play button do not prove playback");
Assert(!(await PlaybackCheck(playbackScreen, .99, .99, .02, "p2")).Achieved, "correct active track in the wrong application does not complete the request");
Assert(!(await PlaybackCheck(playbackScreen, .99, .99, .99, "none")).Achieved, "positive playback probabilities still require selected observed evidence");
Assert(!(await PlaybackCheck(playbackScreen, .849, .99, .99, "p2")).Achieved, "atomic playback checks retain the 0.85 completion threshold");

{
    const string mixedGoal = "Play sweater weather on spotify and set the volume to 20%";
    var modeCalls = 0;
    var genericCalls = 0;
    using var client = new JevClient("fake-test-key", new Handler(async request =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        var questions = body.RootElement.GetProperty("questions");
        if (questions.TryGetProperty("mode", out var mode))
        {
            modeCalls++;
            Assert(mode.GetProperty("criteria").GetProperty("other").GetString()!.Contains("additional task"),
                "mixed requests are explicitly offered generic verification");
            return Reply(new { mode = new { choice = "other", confidence = 1 } });
        }
        genericCalls++;
        Assert(questions.TryGetProperty("completed", out _) && !questions.TryGetProperty("playing", out _) &&
            body.RootElement.GetProperty("state").GetProperty("userGoal").GetString() == mixedGoal,
            "mixed-goal route preserves the complete goal for generic verification");
        return Reply(new { completed = new { noul = .25 }, evidence = new { choice = "p2", confidence = .99 } });
    }, autoMode: false));
    var first = await client.VerifyAsync(mixedGoal, playbackScreen, [], default);
    var second = await client.VerifyAsync(mixedGoal, playbackScreen, [], default);
    Assert(!first.Achieved && !second.Achieved && modeCalls == 1 && genericCalls == 2,
        "mixed request with unproven volume setting remains incomplete and caches only its verification mode");
}
{
    var genericCalls = 0;
    using var client = new JevClient("fake-test-key", new Handler(async request =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        if (body.RootElement.GetProperty("questions").TryGetProperty("mode", out _))
            return Reply(new { mode = new { choice = "playback", confidence = .2 } });
        genericCalls++;
        Assert(body.RootElement.GetProperty("questions").TryGetProperty("completed", out _),
            "uncertain playback mode falls back to full-goal verification");
        return Reply(new { completed = new { noul = .2 }, evidence = new { choice = "none", confidence = 1 } });
    }, autoMode: false));
    var result = await client.VerifyAsync("play sweater weather on spotify", playbackScreen, [], default);
    Assert(!result.Achieved && genericCalls == 1, "low-confidence routing cannot narrow completion requirements");
}

Console.WriteLine("Deterministic component suite passed. The suite above used a fake HTTP transport and did not operate the desktop.");

if (args.Contains("--live"))
{
    using var client = new JevClient();
    var decision = await client.DecideAsync("play sweater weather on spotify", snapshot, [], candidates, default);
    Console.WriteLine("LIVE selection: " + JsonSerializer.Serialize(decision));
    if (decision.TextId is not null)
        Console.WriteLine("LIVE selected text: " + candidates.Single(c => c.Id == decision.TextId).Text);
    var notPlaying = snapshot with { Controls = [field with { Value = "sweater weather" }, new("e2", "Text", "Sweater Weather — The Neighbourhood", "", "Search results", "", new(10, 80, 200, 30), [], true, false), new("e3", "Button", "Play Sweater Weather", "", "Search results", "", new(210, 80, 50, 30), ["invoke"], true, false)] };
    var failure = await client.VerifyAsync("play sweater weather on spotify", notPlaying, [], default);
    Console.WriteLine("LIVE not-playing check: " + JsonSerializer.Serialize(failure));
    Assert(!failure.Achieved, "live Jev rejects mere search results as playback completion");
    var playing = snapshot with { Controls = [new("e2", "Text", "Sweater Weather — The Neighbourhood", "Currently playing", "Now playing", "", new(10, 80, 200, 30), [], true, false), new("e3", "Button", "Pause", "", "Playback controls", "", new(210, 80, 50, 30), ["invoke"], true, false)] };
    var success = await client.VerifyAsync("play sweater weather on spotify", playing, [], default);
    Console.WriteLine("LIVE playing check: " + JsonSerializer.Serialize(success));
    Assert(success.Achieved, "live Jev recognizes matching current track and Pause");
    var paused = playing with { Controls = [playing.Controls[0], playing.Controls[1] with { Name = "Play" }] };
    var pausedCheck = await client.VerifyAsync("play sweater weather on spotify", paused, [], default);
    Console.WriteLine("LIVE paused check: " + JsonSerializer.Serialize(pausedCheck));
    Assert(!pausedCheck.Achieved, "live Jev rejects the right track while paused");
    var wrongTrack = playing with { Controls = [playing.Controls[0] with { Name = "Softcore - The Neighbourhood" }, playing.Controls[1]] };
    var wrongTrackCheck = await client.VerifyAsync("play sweater weather on spotify", wrongTrack, [], default);
    Console.WriteLine("LIVE wrong-track check: " + JsonSerializer.Serialize(wrongTrackCheck));
    Assert(!wrongTrackCheck.Achieved, "live Jev rejects a different track while playing");
    var mixedCheck = await client.VerifyAsync("play sweater weather on spotify and set volume to 20%", playing, [], default);
    Console.WriteLine("LIVE mixed-goal check: " + JsonSerializer.Serialize(mixedCheck));
    Assert(!mixedCheck.Achieved, "live Jev requires evidence of the additional volume-setting goal");
}

static HttpResponseMessage Reply(object answers) => new(HttpStatusCode.OK)
{ Content = new StringContent(JsonSerializer.Serialize(new { model = "test", answers })) };

static HttpResponseMessage ContextExceeded() => new(HttpStatusCode.BadRequest)
{ Content = new StringContent("{\"detail\":{\"error_type\":\"max_tokens_exceeded\"}}") };

sealed class Handler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response;
    private readonly bool autoMode;
    public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response, bool autoMode = true) : this((request, _) => response(request), autoMode) { }
    public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response, bool autoMode = true)
    { this.response = response; this.autoMode = autoMode; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (autoMode)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (body.RootElement.GetProperty("questions").TryGetProperty("mode", out _))
                return new(HttpStatusCode.OK)
                { Content = new StringContent("{\"model\":\"test\",\"answers\":{\"mode\":{\"choice\":\"other\",\"confidence\":1}}}") };
        }
        return await response(request, cancellationToken);
    }
}
