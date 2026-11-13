# Screenshots & video capture checklist

The screenshots in the README must be captured from the **real Windows application**. Do not replace them with mockups or generated interface images.

## Before recording

1. Build and launch the app on Windows 10/11.
2. Use a clean test profile and non-sensitive sample data.
3. Hide or redact Steam IDs, private server addresses, API keys, bot tokens, webhook URLs, pairing codes and personal notifications.
4. Set the display to 1920×1080 (or 1600×900), Windows scaling to 100–125%, and maximize the app.
5. Close unrelated windows and disable desktop notifications.

## Screenshot list

Save PNG files in `docs/media/` using these names:

| File | What to capture | Suggested framing |
|---|---|---|
| `01-dashboard.png` | Overview / command center | Full app window |
| `02-servers.png` | Server list and server details | Include useful status and player count, if available |
| `03-player-monitor.png` | Watchlist and recent observations | Use fictional/test identities or redact IDs |
| `04-timers-events.png` | Timers, Bradley and event history | Show at least one running timer |
| `05-rustplus-devices.png` | Rust+ device panel | Use a test pairing and safe sample device |
| `06-overlay.png` | In-game overlay | Capture over a permitted desktop/game scene |
| `07-settings.png` | Settings and integrations | Never show credentials or secret fields |

Recommended image size: 1600×900 or 1920×1080. Use PNG, crop away unrelated desktop clutter, and keep text readable.

After capture, replace the screenshot placeholders in the root `README.md` with links such as:

```markdown
![Rust Companion dashboard](docs/media/01-dashboard.png)
```

## Video overview

Recommended deliverable: 60–90 second 1080p MP4 (16:9), 30 or 60 FPS, H.264. Keep the edit direct and avoid showing private credentials or identifiers.

### Suggested timeline / narration

| Time | Picture | Narration / on-screen text |
|---|---|---|
| 0:00–0:06 | Logo/banner and app launch | “Rust Companion — a desktop toolkit for monitoring your Rust sessions.” |
| 0:06–0:18 | Main dashboard and navigation | “A single place for servers, timers, events and Rust+ tools.” |
| 0:18–0:30 | Server browser | “Find and save servers, then inspect available server information.” |
| 0:30–0:42 | Player watchlist | “Keep a local observation history for selected Steam players.” |
| 0:42–0:56 | Timers and event tracking | “Record events and use configurable timers based on your observations.” |
| 0:56–1:10 | Rust+ devices and overlay | “Connect supported Rust+ devices and configure notifications.” |
| 1:10–1:20 | Settings / security note | “Your local data stays on your PC. The app does not inject into Rust or bypass EAC.” |
| 1:20–1:25 | GitHub URL / closing card | “Explore the source and setup instructions on GitHub.” |

### Recording notes

- OBS Studio is suitable for screen capture; record the app window rather than the entire desktop.
- Record narration separately if the room is noisy.
- Use short crossfades only; do not speed up UI interactions so much that they become unreadable.
- Export `docs/media/rust-companion-overview.mp4` only if repository size is acceptable. Otherwise upload it to GitHub Releases or a video host and link it from the README.
- Do not claim a feature works unless it has been demonstrated successfully in the recording.

## README video link

Once the video exists, add a link near the screenshot gallery:

```markdown
[▶ Watch the Rust Companion overview](docs/media/rust-companion-overview.mp4)
```
