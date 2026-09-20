# Spotify demo methodology

Recorded on Windows on 20 September 2026. [View the animated preview](preview.gif) or [download the MP4](https://github.com/chand45/JetDesk/raw/refs/heads/master/docs/demo/jetdesk-vs-codex.mp4).

Both workflows received the request:

> play sweater weather on spotify

| Workflow | Elapsed time | Configuration | Observed output |
|---|---:|---|---|
| JetDesk | 17.401 seconds | Jev 1.13.0; native UI Automation | Sweater Weather by The Neighbourhood, music-video mode |
| Codex Computer Use | 42.653 seconds | gpt-6-astra; ultra reasoning; native Computer Use | Sweater Weather by The Neighbourhood, audio mode |

Codex's measured interval was **2.45 times as long**; JetDesk finished **25.3 seconds sooner**. This is one local task demonstration, not a general performance or reliability benchmark. Exactly one measured attempt per workflow was recorded, in the order JetDesk then Codex. No measured reruns were selected for the video.

## Setup and timing

Both used the same running, authenticated native Spotify window, account, geometry, and playback device. Each started with **Rein Me In** paused, **rein me in** in the search field, and the right sidebar collapsed. The functional starting state matched; the paused position differed slightly, at approximately 2:31 and 2:29. Codex's native Computer Use tools were initialized before timing.

- **JetDesk:** the timer starts immediately before launching its existing CLI with the selected Spotify window. It stops at the terminal `completed` event following fresh-screen verification. Process startup, observations, Jev requests, input, settling delays, and verification are included. The normal GUI's three-second countdown is not part of the CLI path.
- **Codex:** the timer starts immediately before its first fresh observation and stops at the returned screenshot showing the requested current song, artist, and global Pause control. Reasoning, tool round trips, input, and additional observations are included. There were no intentional sleeps or unrelated tasks within this interval.

Setup, authentication, prompt entry, and the subsequent chat response are excluded. The two timers therefore measure the configured automation workflows, not total cold-start user experience or model inference alone.

JetDesk dispatched three successful inputs and rejected one further attempt because its target control had changed. That attempt's time remains included. Codex dispatched three inputs. Its accessibility text lagged the actual app, so fresh screenshots supplied its completion evidence; the extra observation time remains included.

## Verification and video editing

The requested current song and artist, global Pause control, and subsequently advancing playback position were independently observed for both runs. Follow-up progress checks occurred after the primary timed endpoints. The muted video establishes visible playback, not recorded speaker output.

The 59.67-second MP4 is 1920 × 1080 at 30 fps. The two recorded start boundaries are aligned, and the measured workflows play at real speed with no waits removed. Each timer stops at its measured endpoint; its footage continues for three seconds to show playback progression, then freezes. Titles and result cards are outside the timed comparison. Raw capture timestamps preserve elapsed time despite dropped capture frames; displayed times are rounded to tenths.

The README includes the full video as a 960 × 540 animated GIF at 10 fps because GitHub's repository file page does not preview this MP4. The GIF preserves the same real-time sequence; the linked MP4 provides the full-quality version.

## Interpretation limits

The task did not specify a playback mode. JetDesk opened a 4:12 music video, while Codex opened a 4:00 audio track. Both displayed the requested song and artist, but identical media assets were not established. The different media paths, fixed run order, cached application state, service latency, observation tools, and Codex's ultra reasoning setting limit attribution and generalization of the difference.

The run used JetDesk source commit `ebced3cf983221dd2390ebf80f726e9311b0410c`. Jev's response logs identified `jev-1.13.0`; Codex session metadata identified `gpt-6-astra` and `ultra`. Source behavior and historical functional checks are documented in the [project verification report](../../VERIFICATION.md).

The published assets are the finished video, preview image, and this methodology. Raw desktop traces and local session records are not included in the repository.
