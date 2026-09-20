using System.Diagnostics;
using System.Text.Json;

namespace Jev.TestMusic;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i].StartsWith("--", StringComparison.Ordinal)) values[args[i++]] = args[i];
        var eventPath = values.GetValueOrDefault("--events") ?? Path.Combine(AppContext.BaseDirectory, "events.jsonl");
        Application.Run(new MusicWindow(eventPath, values.GetValueOrDefault("--ready-file")));
    }
}

internal sealed class MusicWindow : Form
{
    private readonly string eventPath;
    private readonly string? readyPath;
    private readonly TextBox searchBox;
    private readonly FlowLayoutPanel results;
    private readonly Label status;
    private readonly Label selection;
    private readonly Button play;
    private readonly Label elapsed;
    private readonly System.Windows.Forms.Timer timer;
    private string? selectedTrack;
    private int elapsedSeconds;
    private bool playing;

    private static readonly string[] Tracks =
    [
        "Sweater Weather — The Neighbourhood",
        "Sweater Weather (Cover) — Acoustic Sessions",
        "Softcore — The Neighbourhood",
        "Weather With You — Crowded House",
    ];

    public MusicWindow(string eventPath, string? readyPath)
    {
        this.eventPath = Path.GetFullPath(eventPath);
        this.readyPath = readyPath is null ? null : Path.GetFullPath(readyPath);
        Directory.CreateDirectory(Path.GetDirectoryName(this.eventPath)!);
        Text = "Jev Test Music";
        Name = "JevTestMusic";
        AccessibleName = Text;
        ClientSize = new Size(780, 570);
        MinimumSize = new Size(680, 580);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 11);
        BackColor = Color.FromArgb(247, 249, 252);

        var title = new Label
        {
            Text = "Jev Test Music", Name = "title", AutoSize = true,
            Location = new Point(26, 24), Font = new Font("Segoe UI", 20, FontStyle.Bold)
        };
        var explanation = new Label
        {
            Text = "Native Windows UI test fixture · simulated playback", Name = "description",
            AutoSize = true, Location = new Point(28, 68), ForeColor = Color.DimGray
        };
        var searchLabel = new Label
        {
            Text = "Search songs", Name = "searchLabel", AutoSize = true, Location = new Point(28, 109)
        };
        searchBox = new TextBox
        {
            Name = "searchQuery", AccessibleName = "Search songs", AccessibleDescription = "Enter a song title or artist",
            Location = new Point(28, 137), Size = new Size(570, 32), TabIndex = 0
        };
        searchBox.TextChanged += (_, _) => WriteEvent("query_changed", new { query = searchBox.Text });
        var search = new Button
        {
            Name = "searchButton", Text = "Search", AccessibleName = "Search", Location = new Point(610, 135),
            Size = new Size(130, 36), TabIndex = 1
        };
        search.Click += (_, _) => Search();
        AcceptButton = search;
        status = new Label
        {
            Name = "searchStatus", Text = "Search for a song to begin.",
            AutoSize = true, Location = new Point(28, 186)
        };
        results = new FlowLayoutPanel
        {
            Name = "searchResults", AccessibleName = "Search results", Location = new Point(28, 217),
            Size = new Size(714, 168), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true
        };
        selection = new Label
        {
            Name = "selectedTrack", Text = "No song selected", AutoSize = true,
            Location = new Point(28, 410)
        };
        play = new Button
        {
            Name = "playButton", Text = "Play selected song", AccessibleName = "Play selected song",
            Location = new Point(28, 451), Size = new Size(190, 40), Visible = false, TabIndex = 5
        };
        play.Click += (_, _) => PlaySelected();
        elapsed = new Label
        {
            Name = "playbackTime", Text = "", AutoSize = true,
            Location = new Point(240, 461)
        };
        Controls.AddRange([title, explanation, searchLabel, searchBox, search, status, results, selection, play, elapsed]);
        timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) =>
        {
            elapsedSeconds++;
            elapsed.Text = $"Playing · {TimeSpan.FromSeconds(elapsedSeconds):m\\:ss}";
            if (elapsedSeconds <= 3 || elapsedSeconds % 5 == 0)
                WriteEvent("playback_tick", new { track = selectedTrack, elapsedSeconds });
        };
        Shown += (_, _) =>
        {
            WriteEvent("window_ready", new { pid = Environment.ProcessId, handle = Handle.ToInt64(), title = Text });
            if (this.readyPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this.readyPath)!);
                File.WriteAllText(this.readyPath, JsonSerializer.Serialize(new
                {
                    pid = Environment.ProcessId, handle = Handle.ToInt64(), title = Text, events = this.eventPath
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            searchBox.Focus();
        };
        FormClosed += (_, _) =>
        {
            timer.Stop();
            WriteEvent("window_closed", new { track = selectedTrack, playing, elapsedSeconds });
        };
    }

    private void Search()
    {
        timer.Stop();
        playing = false;
        selectedTrack = null;
        elapsedSeconds = 0;
        elapsed.Text = "";
        selection.Text = "No song selected";
        selection.AccessibleName = selection.Text;
        play.Visible = false;
        results.Controls.Clear();
        var query = searchBox.Text.Trim();
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = words.Length == 0
            ? []
            : Tracks.Where(track => words.All(word => track.Contains(word, StringComparison.OrdinalIgnoreCase))).ToArray();
        WriteEvent("search", new { query, resultCount = matches.Length });
        status.Text = query.Length == 0 ? "Enter a song title or artist to search."
            : matches.Length == 0 ? $"No songs found for '{query}'."
            : $"{matches.Length} songs found. Select a song, then press Play.";
        status.AccessibleName = status.Text;
        foreach (var track in matches)
        {
            var button = new Button
            {
                Name = "track_" + Array.IndexOf(Tracks, track), Text = track,
                AccessibleName = "Select " + track, AccessibleDescription = "Search result. Select this track before playback.",
                Size = new Size(680, 43), Margin = new Padding(0, 0, 0, 6), TextAlign = ContentAlignment.MiddleLeft
            };
            button.Click += (_, _) =>
            {
                selectedTrack = track;
                selection.Text = "Selected: " + track;
                selection.AccessibleName = selection.Text;
                play.Visible = true;
                play.Text = "Play selected song";
                play.AccessibleName = "Play selected song";
                status.Text = "Ready to play the selected song.";
                status.AccessibleName = status.Text;
                WriteEvent("select_track", new { track });
            };
            results.Controls.Add(button);
        }
    }

    private void PlaySelected()
    {
        if (selectedTrack is null) return;
        if (playing)
        {
            timer.Stop();
            playing = false;
            play.Text = "Resume playback";
            play.AccessibleName = "Resume playback";
            status.Text = "Paused: " + selectedTrack;
            status.AccessibleName = status.Text;
            WriteEvent("pause", new { track = selectedTrack, elapsedSeconds });
            return;
        }
        playing = true;
        status.Text = "Now playing: " + selectedTrack;
        status.AccessibleName = status.Text;
        selection.Text = "Playing: " + selectedTrack;
        selection.AccessibleName = selection.Text;
        play.Text = "Pause playback";
        play.AccessibleName = "Pause playback";
        elapsed.Text = $"Playing · {TimeSpan.FromSeconds(elapsedSeconds):m\\:ss}";
        WriteEvent("play", new { track = selectedTrack, elapsedSeconds });
        timer.Start();
    }

    private void WriteEvent(string operation, object details)
    {
        File.AppendAllText(eventPath, JsonSerializer.Serialize(new
        {
            time = DateTimeOffset.UtcNow, operation, details
        }) + Environment.NewLine);
    }
}
