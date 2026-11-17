# Plan: Remote transcoding through a server agent

Status: Draft · Branch `feature/remote-agent`

## 1. Goal

FFApp remains the desktop app on the Mac. Compute-intensive transcoding jobs can run on a headless
Linux server in the home network instead. An **FFApp agent** runs there permanently. The app uploads
the source file to the agent and hands over the job definition. The agent encodes with its own
FFmpeg. The app then either downloads the result to the Mac or leaves it on the server.

A running job does not depend on the Mac. The Mac may sleep, be closed or be switched off while the
server encodes. Only the upload, and the download if one is needed, require the Mac.

### Non-goals of the first version

- Several jobs at once on the server (the agent runs jobs strictly one after another; `MaxConcurrentJobs` is prepared)
- Comparison window and test clips on the server
- FFRux remuxing through the agent
- TLS, multiple agents, a Docker image, unattended agent updates (updates run only after a click in the app)
- Access from outside the home network (use a VPN such as Tailscale later, not port forwarding)

## 2. Decisions

| Topic | Decision |
|---|---|
| File transfer | HTTP through the agent; resumable upload, download with `Range` |
| Result | Chosen per job: **download to the Mac** or **keep on the server** |
| App ↔ agent contract | The app sends `JobDefinition` + `SourceMediaInfo`; the agent builds the commands itself |
| Security | Secret API token (Bearer), plain HTTP inside the LAN |
| Operation | Self-contained Linux binary + systemd unit + install script |
| Agent updates | Triggered by a click in the app; the agent installs exactly the app's version |
| Selection in the app | **Local / Server** toggle when queueing; default in settings; target badge in the queue |
| Scope of v1 | Includes pause/resume on the server |
| Storage | Work directory with a quota; sources deduplicated by fingerprint |

## 3. Architecture

```
 Mac (FFApp)                                     Ubuntu server (FFApp agent, systemd)
 ┌───────────────────────────────┐   HTTP/LAN   ┌────────────────────────────────────┐
 │ QueueService                  │              │ Kestrel  /api/v1/...               │
 │  ├─ local lane ──► Process-   │              │  ├─ SourceStore (uploads, quota)   │
 │  │                 Runner     │  upload ───► │  ├─ AgentJobQueue (persisted)      │
 │  └─ server lane ─► Remote-    │  job    ───► │  │   └─ DefaultJobExecutionService │
 │                    JobExec.   │  ◄── SSE     │  │        └─ ProcessRunner ► ffmpeg │
 │ RemoteAgentClient             │  ◄── download│  └─ EventHub (server-sent events)  │
 │ RemoteJobStore (persisted)    │              │ /var/lib/ffapp-agent (work dir)    │
 └───────────────────────────────┘              └────────────────────────────────────┘
          FFApp.Encoding  ◄──────────── shared ────────────►  FFApp.Encoding
          FFApp.Remote.Contracts ◄─────────────────────────►  FFApp.Remote.Contracts
```

### New and changed projects

| Project | Contents |
|---|---|
| `FFApp.Encoding` (new, no UI) | Moved out of `src/FFApp`: `Core/CommandBuilder.cs`, `Core/Models/*` (job and settings models), `Core/Validation/*`, `Core/OutputNaming.cs`, `Infrastructure/JobExecutionService.cs`, `MatroskaPostProcessor.cs`, `FfmpegCapabilityService.cs` and their interfaces. Depends only on `FFApp.Media`. |
| `FFApp.Remote.Contracts` (new) | Protocol DTOs, `ProtocolVersion`, a source-generated `JsonSerializerContext` |
| `FFApp.Agent` (new) | ASP.NET Core minimal API, job queue worker, systemd integration (`UseSystemd()`) |
| `FFApp` | `RemoteAgentClient`, `RemoteJobExecutionService`, `RemoteJobStore`, `QueueService` changes, settings and UI |
| `tests/FFApp.Agent.Tests` (new) | Tests through `WebApplicationFactory` with a fake `IProcessRunner` |

Checked with grep: the classes to be moved use neither Avalonia nor LibVLC nor FFmpeg.AutoGen. Only
the comparison and preview code does, and it stays in `FFApp`.

## 4. Flow of a server job

```
Queue (target = server)
  │
  ▼
Validate ── app calls GET /info: validation against the AGENT's FFmpeg capabilities
  │
  ▼
Upload ── fingerprint → POST /sources → already present? then skip
  │       otherwise PUT chunks (resumable); SHA-256 is computed while sending
  ▼
Submit ── POST /jobs { sourceId, job, mediaInfo, resultMode, serverOutputRoot? }
  │       from here on the job runs independently of the Mac
  ▼
Wait/encode ── progress and log lines through SSE; pause/resume/cancel through POST
  │
  ├─ resultMode = Download ──► download (Range, resumable) to the local output path
  │                             → POST /jobs/{id}/ack → agent cleans up
  │
  └─ resultMode = KeepOnServer ──► file stays in an allowed server folder → done
```

Phases in the app: `Uploading → Waiting for server → Running → Downloading → Done`.
The phase is an additional `RemotePhase` field in `JobProgress`. `JobState` gets no new values, so
the existing state logic and color converters stay unchanged.

## 5. Protocol (REST, `/api/v1`)

Every endpoint except `GET /health` requires `Authorization: Bearer <token>`.

| Method & path | Purpose |
|---|---|
| `GET /health` | Liveness without authentication, for systemd/monitoring |
| `GET /info` | Agent version, `ProtocolVersion`, FFmpeg version and capabilities, free space, quota, allowed output folders |
| `POST /sources` | Register an upload: `{ fingerprint, size, fileName }` → `{ sourceId, receivedBytes, complete }` |
| `PUT /sources/{id}` | Data chunk with `Content-Range`; the server checks the offset |
| `POST /sources/{id}/complete` | `{ sha256 }`; the server compares the hash and marks the source complete |
| `POST /jobs` | Create a job: `{ clientJobId, sourceId, job: JobDefinition, mediaInfo, resultMode, serverOutputRoot? }` |
| `GET /jobs` · `GET /jobs/{id}` | State of all jobs or one job, also used to reconcile after the app restarts |
| `GET /jobs/{id}/log` | Full job log |
| `POST /jobs/{id}/cancel` · `/pause` · `/resume` | Control |
| `GET /jobs/{id}/output` | Result download with `Range` and `ETag` |
| `POST /jobs/{id}/ack` | Download confirmed → agent deletes the result and releases the source once nothing needs it |
| `DELETE /jobs/{id}` | Remove a job from the list (only when it is not running) |
| `GET /events` | Server-sent events: `job-state`, `job-progress`, `job-log`, `source-progress`, `update-state` |
| `POST /update` | Install a given version: `{ version }` → `202 Accepted`; see section 6, "Updates" |
| `GET /update` | Update state: `idle`, `waiting-for-jobs`, `downloading`, `restarting`, `failed` (with message) |

Rationale:

- **SSE instead of SignalR:** ASP.NET Core 10 provides SSE through `TypedResults.ServerSentEvents`,
  and the client reads it with the small Microsoft package `System.Net.ServerSentEvents`. If the
  connection drops, the app reconnects and reconciles through `GET /jobs`.
- **`clientJobId`** (the app's queue ID) makes `POST /jobs` idempotent. If the connection drops
  between sending and the response, no duplicate job is created.
- **Version check:** the app requires the same `ProtocolVersion` (an integer). On a mismatch the
  "Server" target is blocked and the app offers "Update agent to <app version>". If only the
  release version differs and the protocol matches, the app keeps working and shows the same offer
  as a non-blocking hint.
- **`JobDefinition` over the wire:** the models are already `record`s. Enums need stable names on
  the wire. Per `docs/coding-style.md` this is done per type through
  `[JsonConverter]`/`[JsonStringEnumMemberName]`, not globally. A round-trip test ensures every
  property arrives.

## 6. Agent details

### Work directory

```
/var/lib/ffapp-agent/
  versions/<version>/ffapp-agent      # installed agent versions (current + previous)
  current -> versions/<version>       # symlink started by systemd
  tools/                              # optional static FFmpeg 9 build (install.sh --with-ffmpeg)
  sources/<sourceId>/source.<ext>     # uploaded source (+ .partial during the upload)
  sources/<sourceId>/meta.json        # fingerprint, SHA-256, reference count
  jobs/<jobId>/job.json               # state (written atomically: tmp + rename)
  jobs/<jobId>/work/                  # two-pass logs, extracted images (the job's TMPDIR)
  jobs/<jobId>/output.<ext>           # result when resultMode = Download
  jobs/<jobId>/job.log
```

- **Path rewriting:** the agent replaces `SourcePath` with the uploaded file and `OutputPath` with
  `jobs/<id>/output.<ext>` or `<serverOutputRoot>/<file name>`. The file name comes from the app
  (`OutputNaming`). The agent uses only the name without a directory and rejects `..` and path
  separators.
- **Temporary files:** `CommandBuilder` currently writes extracted images to `Path.GetTempPath()`.
  The temp directory should become injectable instead, so that each job uses its own `work/` and
  cleanup stays reliable even after a crash.
- **Persistent state:** one JSON file per job, which is enough for a single user and a few jobs.
  If the agent restarts while a job is running, the job becomes `failed` ("Agent restarted"). The
  source is kept, so "Run again" works without a new upload.
- **Execution:** the same `DefaultJobExecutionService` as on the Mac, with the agent's FFmpeg paths
  and capabilities. Pause and resume use the existing `ProcessSuspension` (it already supports
  Linux signals). The process runs with `Nice=10` so the server stays responsive.
- **Quota and deduplication:** before `POST /sources` the agent checks `size` against
  `QuotaGiB − used` and against free disk space. The fingerprint consists of size, modification
  time and the hash of the first and last 16 MiB; the fingerprint logic of the comparison cache
  (`ComparisonService.Cache.cs`) can be reused. Several jobs with the same source upload it only
  once. A reference count deletes the source after the last `ack` or the last `DELETE`.
- **Retention:** the agent deletes results without an `ack` and orphaned sources after
  `RetentionDays` (default 14).

### Configuration (`/etc/ffapp-agent/appsettings.json` → `IOptions<AgentOptions>`)

```json
{
  "Agent": {
    "Urls": "http://0.0.0.0:8737",
    "WorkDirectory": "/var/lib/ffapp-agent",
    "QuotaGiB": 500,
    "MaxConcurrentJobs": 1,
    "RetentionDays": 14,
    "ApiTokenSha256": "<hash, set by the installer>",
    "OutputRoots": { "Movies": "/srv/media/movies", "Shows": "/srv/media/shows" },
    "Tools": { "Ffmpeg": "", "Ffprobe": "", "Mkvpropedit": "" }
  }
}
```

`OutputRoots` is an allowlist with display names. In "keep on server" mode the app only picks a
name, never a free path.

### Release assets

The release workflow additionally publishes, for every version:

- `ffapp-agent-<version>-linux-x64.tar.gz` and `ffapp-agent-<version>-linux-arm64.tar.gz`
  (self-contained, single file)
- `ffapp-agent-install.sh` (the same script for every architecture)
- `SHA256SUMS` covering the agent archives

### Bootstrapping

One command on the server installs the latest release, or a fixed version:

```bash
curl -fsSL https://github.com/rezun/ffapp/releases/latest/download/ffapp-agent-install.sh | sudo bash
curl -fsSL https://github.com/rezun/ffapp/releases/download/v1.15.0/ffapp-agent-install.sh | sudo bash -s -- --version 1.15.0
```

`install.sh` is idempotent. It:

1. Detects the architecture (`uname -m` → `linux-x64` or `linux-arm64`).
2. Downloads the archive and `SHA256SUMS` from the GitHub release and verifies the checksum.
3. Creates the system user `ffapp-agent` (no login shell, no sudo).
4. Extracts the agent to `/var/lib/ffapp-agent/versions/<version>/` and points `current` at it.
5. Writes `/etc/ffapp-agent/appsettings.json` only if it does not exist yet, so later runs keep
   the configuration.
6. On the first run, generates a random token, stores only its SHA-256 hash and prints the token
   **once**. `ffapp-agent token reset` creates a new one later.
7. Installs the systemd unit and runs `systemctl enable --now ffapp-agent`.
8. Runs `ffapp-agent check`, which verifies FFmpeg ≥ 9, the encoders/filters and `mkvpropedit`
   like the existing `DependencyManager`, and prints the result.

Systemd unit essentials:

```ini
[Service]
User=ffapp-agent
ExecStartPre=/var/lib/ffapp-agent/current/ffapp-agent startup-check
ExecStart=/var/lib/ffapp-agent/current/ffapp-agent serve
Restart=always
Nice=10
ProtectSystem=strict
ReadWritePaths=/var/lib/ffapp-agent <output roots>
NoNewPrivileges=yes
```

`install.sh` writes the `ReadWritePaths` line from the configured `OutputRoots`. After changing
`OutputRoots` the user runs `install.sh` again (it only regenerates the unit) or edits the unit.

**FFmpeg 9 on Ubuntu:** distribution packages lag behind (Ubuntu 24.04 ships FFmpeg 6.1). The agent
only needs the `ffmpeg` and `ffprobe` executables, not the shared libraries used for comparison
playback. `install.sh --with-ffmpeg` downloads a static FFmpeg 9 build into
`/var/lib/ffapp-agent/tools/` and sets `Tools` in the configuration. Without the option the agent
uses the system FFmpeg and `ffapp-agent check` reports whether it is new enough.

**Pairing:** in FFApp, *Settings → Server agent…* takes the URL and the token; "Test connection"
calls `GET /info`. Optional: the agent announces itself through mDNS/Bonjour
(`_ffapp-agent._tcp`), so the app can prefill the URL and only the token has to be typed.

### Updates

The app decides which version the agent runs. App and agent exchange `JobDefinition` directly, so
every new encoder option effectively changes the contract. If the agent updated itself to the
latest release, it could get ahead of an app whose Velopack update only applies at the next
restart. The agent therefore installs exactly the version the app asks for, and only after the
user clicks.

1. FFApp updates itself through Velopack as today.
2. When connecting, the app compares its version with the agent's version from `GET /info`.
3. If they differ, the app shows "Update agent to <app version>" in the server agent settings and
   as a hint next to the **Local | Server** toggle. Nothing happens without a click.
4. The click sends `POST /update { version }`.
5. The agent waits until no job is running. New jobs are accepted meanwhile but not started.
   Uploads and downloads continue; both are resumable anyway.
6. The agent downloads the archive of that version, verifies the checksum and extracts it to
   `versions/<version>/`.
7. It switches `current` to the new version, remembers the previous one in `previous`, and exits.
   systemd restarts it immediately (`Restart=always`).
8. `ExecStartPre=… startup-check` guards the start: if the new version fails to start twice in a
   row, it switches `current` back to `previous`, and `GET /update` reports `failed` with the
   reason.
9. The app follows the progress through `update-state` events, sees the new version in
   `GET /info` and enables the server target again.

Notes:

- **No root needed:** all versions live under `/var/lib/ffapp-agent/`, which belongs to the service
  user and stays writable despite `ProtectSystem=strict`. The configuration in `/etc` and the
  systemd unit are not touched by updates.
- **Downgrade** works the same way, because the app asks for its own version. This helps when a
  release has problems.
- **Retention of versions:** the agent keeps `current` and `previous` and deletes older versions
  after a successful start.
- **Configuration changes:** new settings get defaults in code. A version that needs a migration
  of `appsettings.json` must say so in `ffapp-agent check`; the app shows that output.
- **Manual fallback:** running `install.sh` again (with or without `--version`) always works,
  for example when the agent is too old to understand `POST /update`.
- **Trust:** downloading and running code from GitHub releases adds no new trust relationship,
  because the app's Velopack updater already trusts the same source. While releases are unsigned,
  the SHA-256 check only protects against corrupted downloads, not against a tampered release.

Rejected alternatives:

| Option | Why not |
|---|---|
| `.deb` package or own apt repository | Clean first install, but updates stay manual and a repository needs hosting and signing. Possible later addition. |
| Velopack for the agent | Aimed at desktop apps (AppImage on Linux), not at a systemd service. |
| Agent updates itself to the latest release | Can get ahead of the app (see above). |
| App updates the agent over SSH | Requires SSH keys and sudo on the server: more attack surface for the same result. |

## 7. App details

### Queue

`QueueService` gets two lanes:

- **Local lane:** unchanged, one job after another.
- **Server lane:** uploads run one after another, because a single upload already saturates the
  gigabit link. As soon as an upload finishes, the job is submitted to the agent and the next
  upload starts. Downloads run in parallel with uploads.

Both lanes run at the same time, so a local copy or remux job does not wait for an AV1 job on the
server. The existing factory `Func<QueuedJob, IJobExecutionService>` (`App.axaml.cs:113`) returns
`DefaultJobExecutionService` or `RemoteJobExecutionService` depending on the target.

Pause and cancel act per lane. Pause in the queue bar stops both; a context menu on the job allows
targeted control.

### Persistence on the Mac

The local queue is currently not saved. Server jobs, however, must survive an app restart. The
`RemoteJobStore` therefore stores `clientJobId`, `agentJobId`, `sourceId`, the local target path,
`resultMode` and the phase for each server job in `<AppData>/remote-jobs.json`. On startup the app
reconciles this list with `GET /jobs`, shows the jobs in the queue again and resumes unfinished
uploads and downloads.

### Sleep

While an upload or download runs, the app holds a macOS power assertion
(`IOPMAssertionCreateWithName`, like `caffeinate -i`). It needs none while the server is encoding.

### Settings and token

- `AppSettings.RemoteAgent { BaseUrl, DefaultTarget (Local|Server), DefaultResultMode, DefaultServerOutputRoot }`;
  `SchemaVersion` goes to 5.
- The token is not stored in `settings.json` but behind a new `ISecretStore` abstraction:
  - macOS: Keychain (through P/Invoke to `Security.framework`)
  - Windows: DPAPI
  - Linux: a file with `0600` permissions

### UI (see the `avalonia-ux` skill)

- **Settings → Server agent…:** URL, token, and "Test connection", which shows the version, FFmpeg,
  available encoders and free space.
- **Next to "Add to queue":** a **Local | Server** segmented toggle. It is disabled and explains
  why while no agent is set up or reachable. With **Server** selected there is an additional
  choice "Result: To this Mac | On the server: <OutputRoot>".
- **Validation:** when the target is "Server", `FfmpegFeatureValidation` checks against the agent's
  capabilities. An encoder that only the Mac has therefore does not wrongly block, and vice versa.
- **Queue item:** a "Server" badge, phase and progress (e.g. "Uploading 42 % · 108 MB/s",
  "Running 0.8× · 1:12 h left", "Downloading 63 %"). The log shows the agent's lines and is also
  stored locally in the `JobLogStore`.

## 8. Failure cases

| Situation | Behavior |
|---|---|
| Agent unreachable when queueing | "Server" target disabled, with the reason |
| Network drop during upload/download | Automatic retry with backoff, resume from the confirmed offset |
| App quit during upload | Resume on next start (`RemoteJobStore`) |
| App quit during encoding | The job keeps running; the app shows it again on start and downloads the result |
| SHA-256 mismatch | Discard the source, retry the upload once, then fail |
| Quota or disk full | Rejected before the upload, with numbers ("needs 41 GB, 12 GB free") |
| Agent restart during encoding | Job `failed`, "Run again" without a new upload |
| Target file already exists at download | Same conflict handling as locally (`OutputNaming`) |
| Local disk full during download | Error; the result stays on the server, "Download again" |
| Protocol version mismatch | Server target disabled, "Update agent to <app version>" button |
| Agent update fails (download, checksum, start) | Agent keeps or restores the previous version; app shows the reason from `GET /update` |
| Agent too old for `POST /update` | App shows the `install.sh` command for the app's version |

## 9. Implementation phases

1. **Refactoring without behavior change**
   - Extract `FFApp.Encoding`, make the temp directory injectable
   - JSON round-trip tests for `JobDefinition` and `SourceMediaInfo`
2. **Agent MVP**
   - `/info`, resumable upload, `/jobs`, SSE, download, `ack`, cancel
   - Persisted state, token check, tests with `WebApplicationFactory`
3. **App client**
   - Settings and `ISecretStore`, connection test
   - `RemoteJobExecutionService`, queue lanes, progress display
   - `RemoteJobStore` with reconciliation on startup
4. **Completing v1**
   - Pause and resume, "keep on server" mode with `OutputRoots`
   - Deduplication and quota, power assertion, retention
5. **Delivery**
   - Agent archives, `SHA256SUMS` and `install.sh` in the release workflow, systemd unit
   - `--with-ffmpeg`, `POST /update` with rollback, "Update agent" in the app
   - README section, `CHANGELOG.md`

Each phase can be merged on its own. Phase 1 is pure refactoring and needs no changelog entry.

## 10. Open questions and proposals

1. **Port:** proposed `8737`. Is there already a reverse proxy (Caddy, nginx) on the server that
   the agent should sit behind?
2. **Trimmed jobs:** for a short trim the app could upload only the needed range (cut locally
   with `-c copy` and a keyframe margin first). That saves time but is error-prone with subtitles
   and chapters. Proposal: not in v1.
3. **Wake-on-LAN:** should the app be able to wake the server when it sleeps? That is little effort
   if the MAC address is stored in the settings.
4. **Notification:** should the app show a macOS notification when a server job finishes, even
   while the download is still running?
5. **Hardware encoders:** does the server have a GPU (NVENC/QSV/VAAPI)? Then extending the encoder
   list would be interesting later. It is independent of the agent.
6. **Multiple clients:** should other computers (e.g. a Windows PC) use the same agent later? The
   protocol already supports it. The only open question is whether jobs from other clients should
   be visible in the queue (proposal: only your own, filtered by `clientId`).
