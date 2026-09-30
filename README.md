# Git Reviewer

Version **3.1.0** uses native model-driven Git tools and requires a
tool-capable model/provider. There is no legacy diff-prompt fallback.

Git Reviewer is a Windows desktop application that uses a local or cloud
OpenAI-compatible model to inspect Git commits for correctness bugs.

## Features

- Keeps separate repository and branch cursors in `state.json`; selecting a valid new repository registers it without reusing another repository's cursor.
- Reviews the selected branch tip on first connection without scanning older commits, except in local-branch fetch mode, which starts with incoming commits after the local tip.
- Fetches remote updates; a selected checked-out local branch advances by fast-forward after a saved review or explicitly recorded skip, only with a clean working copy.
- Fetch uses `--progress` and streams progress into the journal and status line,
  throttled to roughly one update per second per output stream. Large transfers
  have a one-hour timeout and can be canceled with Stop. Captured stdout/stderr
  retain only their last 16,000 characters, so verbose progress cannot exhaust
  the output limit. Other Git operations keep their existing shorter timeouts.
- Supports local-only repositories without requiring a remote; automatic fetch is skipped when no upstream is configured.
- Supports linked Git worktrees that share one common object store and repository state.
- Supports private remotes through SSH Agent, OpenSSH keys, PuTTY `.ppk` keys, or HTTPS credentials.
- Lets the model independently explore an immutable commit using read-only Git tools.
- Allows manual review of any commit by its short or full SHA.
- The editable commit selector lists the latest 100 commits on the selected branch
  with short SHAs and subjects. Open it to refresh after fetch, or type an older
  SHA manually. The same selection is used for manual review and automatic start.
- Lets an authorized user set the automatic review baseline to an ancestor of the selected branch; monitoring continues after that commit.
- Supports multiple profiles for tool-capable OpenAI-compatible APIs, vLLM, Ollama, and LM Studio.
- Uses an embedded, versioned system prompt and a simple text response format instead of model-generated JSON.
- Provides English and Russian user interfaces and prompts.
- Continues monitoring in the Windows system tray after the main window is closed.
- Writes findings to a Markdown report with commit, file, and line information.
- Provides a separate lifecycle **Log** window and a **Journal** (**Журнал**) tab.
- Records each model-driven Git command with its arguments, exit codes, captured output, and errors in the detailed log.
- Requests a tray notification for each completed manual or automatic commit review, including no findings.

## Crash diagnostics

Unhandled WPF dispatcher, AppDomain, Windows Forms thread and unobserved task
exceptions are recorded in separate `crashes/crash-*.log` files under the application
data directory (by default `%LOCALAPPDATA%/GitReviewer`). Reports include time,
application/runtime version, exception type/message, stack and inner exceptions,
plus the last review context. UI fatal errors show the report path and are not
silently ignored; the process terminates rather than continuing uncertain review state.
Unobserved task exceptions are logged when the runtime raises that event.
If the main directory is unwritable, reports fall back to `%TEMP%/GitReviewer-crashes`.
Reports may contain local paths and exception data; inspect them before sharing.
Forced process termination, power loss and severe runtime failures (for example
stack overflow or insufficient memory to write a report) cannot be guaranteed to log.

The first tab is the **Dashboard**; status is no longer repeated at the bottom.
It displays repository/branch, full current SHA, subject, author/date, the remaining
queue including the current commit, four recent journal entries, elapsed processing
time for the current attempt, and a countdown to the next automatic cycle. The queue
is based on the locally discovered tip (after fetch), not a prediction of future
remote commits. Unknown/loading queue size is shown as a dash. Elapsed time freezes
on completion, failure or cancellation and restarts for the next attempt. The next-run
countdown appears during the poll delay, including the delay after a failed cycle;
during processing it says "After this cycle", and when stopped it shows a dash.

The Dashboard shows separate provider-reported Input and Output token counters
for the commit, today and all time. Both counters persist across restarts. Historical
total-only records are retained but cannot be retroactively split: unavailable
breakdowns show "Unavailable", and partially known counters show "+ ?". Missing
input/output fields from the provider are not treated as known zeroes.

The Dashboard also tracks provider-reported token totals for this repository/commit
(including retries), the current local calendar day, and all reviews since tracking
was enabled. Input and output tokens are counted from API `usage`, never estimated
from character counts; repeated input/context is included as reported by the provider.
Streaming requests set `stream_options.include_usage=true`. A provider that rejects
that standard option must be configured/upgraded to support it; no silent retry is sent.
Latest cumulative usage per response is recorded once, including known usage from
failed/incomplete responses. Requests without usage are counted and explicitly marked
as unknown/incomplete totals, not zero-cost requests. Counts update after each response
when usage is available, not per generated token. No historical usage is invented.
`token-usage.json` in the application data directory persists daily, per-commit and
all-time totals using an atomic replacement. Corrupt history is preserved and reported,
not overwritten with zero counters. Totals cover review requests, not connection tests.

**Clear journal** on the Journal tab confirms and clears `journal.log`, its rotated
copy and the Dashboard's recent entries. New activity may immediately create new
entries. Reports, token statistics and the separate detailed model log are untouched.

## Requirements

- Windows 10 or Windows 11
- .NET 10 SDK to build the application
- Git installed and available in `PATH`
- A local model server or an API key for a cloud model

## Build And Run

```powershell
dotnet build
dotnet run
```

To create one versioned, self-contained Windows x64 executable, run:

```powershell
.\publish-win-x64.ps1
```

The result is written to:

```text
dist\GitReviewer-3.1.0-win-x64.exe
```

The executable includes the .NET runtime and default configuration templates.
It can be moved and launched by itself; no adjacent DLL or template files are
required.

After launch, select a Git repository, configure a model profile, test the
connection, and click **Start**.

Closing or minimizing the window hides it in the system tray. Monitoring keeps
running in the background. Use **Exit** (**Выход**) in the main window or tray
menu to cancel and await manual review, stop background monitoring, close Log,
remove the tray icon, and shut down the application.

## Branch Selection

Choose an existing folder, then select a full ref in **Selected branch**. Use
**Refresh** after creating or fetching branches outside the app. Folder changes
reload the list; stale asynchronous results cannot replace newer selections.
`refs/heads/main` and `refs/remotes/origin/main` are different selections, and Git
ref names are case-sensitive. Symbolic remote HEAD aliases are excluded.

The selection is saved as `branch_ref` in `settings.conf`. Older configurations
without this setting default to the current local branch. A missing selected
branch is an error, never a silent switch to HEAD. A detached checkout requires
an explicit branch selection. Manual SHA review can still inspect any existing
commit, not just commits reachable from that branch; its report uses the selected ref.

With **Run git fetch before each check** enabled, the selected remote-tracking
ref is fetched from its configured remote/source ref. For a local selection,
the current local tip is the baseline, and incoming commits are reviewed in
first-parent order. After the report and cursor are saved, the local branch and
working files advance to that commit using `merge --ff-only`. Exhausted analysis
failures are saved as explicit skips before advancement. The local tip remains authoritative on restart,
including after an interrupted advancement. An explicit older start commit is
reviewed through the local tip before advancing. Merge commits are reviewed
against their first parent; side-branch commits are represented by the merge diff.
Dirty/untracked files, an active Git operation, a different checked-out branch,
diverged history or a baseline off the first-parent chain stop advancement.
Do not run concurrent Git mutations during review. Hooks and autostash are disabled
for advancement; ignored files cannot be overwritten by the merge.
Select `refs/remotes/...` to keep working files unchanged. Fetch itself uses explicit
refspecs and cannot move local branches. Without an upstream, fetch is a no-op.
The legacy configuration key `pull_enabled` is retained, but now means fetch.

Automatic cursors and report names use the full case-sensitive ref. State files
record `SchemaVersion: 3`. Unversioned and version 1 legacy files are migrated atomically on
load: every short local branch name is converted to a full ref, including names
that themselves begin with `refs/heads/`. Versioned keys are never reinterpreted
as short names, and differently cased keys are not merged. Unknown schema versions
are rejected without rewriting the file. Old report files are retained, while
new reports use full-ref identities.

### Linked Worktrees

Folders created with `git worktree add` are supported. The application resolves
the selected worktree root, its administrative Git directory, and the shared
directory returned by `git rev-parse --git-common-dir`. The worktree root remains
the context for committed file and attribute reads, while the normalized common
Git directory identifies repository state, reports, and fetch coordination.

All linked worktrees therefore reuse the same object database. Fetches that
target the same common Git directory are serialized so they cannot compete while
updating shared objects or refs; Git downloads only objects missing from that
store. Branch cursors and pending start commits remain independent because they
are keyed by full case-sensitive refs. No checkout or worktree files are changed.

Schema version 2 repository keys used worktree roots. They are upgraded to
schema version 3 and lazily moved to the common Git directory when a worktree is
selected. States from multiple worktree paths are merged only when duplicate
branch values agree; conflicting cursors or pending starts are rejected instead
of silently choosing one.

The application does not clone repositories. Select an existing local Git
working directory, with or without a configured remote.

## Repository Authentication

The **Project** tab provides four authentication modes:

- **SSH Agent** uses keys already loaded into Windows OpenSSH Agent. This is the
  recommended mode for private keys protected by a passphrase.
- **SSH Key File** passes the selected private key file to OpenSSH. Only the
  path is stored in `settings.conf`; the key is not copied. Add the matching
  public key to the Git server. Use SSH Agent for an encrypted key.
- **PuTTY Key File (.ppk)** runs PuTTY `plink.exe` in batch mode with the
  selected `.ppk` file. Set **Plink executable** using **Browse...** or type its
  path without surrounding quotes. The path is saved as `plink_path` in
  `settings.conf` and used for access tests and background fetch; spaces are
  supported. Leave it empty to search the standard PuTTY installation folders,
  then `PATH`. A nonempty missing path reports an error rather than falling back.
  This setting is enabled only for PuTTY authentication and retained when switching
  modes. Load an encrypted key into Pageant before starting the review.
- **HTTPS** uses credentials already stored by Git Credential Manager. GitHub
  requires a personal access token instead of an account password.

SSH modes require an SSH remote such as:

```text
git@github.com:user/private-repository.git
```

HTTPS mode requires a remote such as:

```text
https://github.com/user/private-repository.git
```

The **Test repository access** button runs:

```powershell
git ls-remote --exit-code <upstream-remote> <tracked-branch-ref>
```

The remote and source ref are resolved from the selected branch, so the test
uses the same remote as fetch (including non-origin remotes and custom fetch mappings).
For a newly registered remote-backed repository, a successful access test also
enables the **Start from selected commit** button. A local-only repository needs
no remote test; local repository and branch validation enables the action. A
repository that was already present in `state.json` also enables it after local
validation.
SSH Agent mode explicitly uses Windows OpenSSH Client and
the Windows `ssh-agent` service instead of Git for Windows' bundled SSH client.
SSH connections run non-interactively. OpenSSH requires the server to already
exist in the user's `known_hosts` file; PuTTY uses its own host-key cache.
Verify and accept the server fingerprint using the corresponding client before
running unattended checks. The application never stores an SSH
passphrase or an HTTPS token itself.

## Review Behavior

On the first connection, only the selected branch tip is reviewed against its first
parent. The model can access only the reviewed SHA and its immediate first parent,
not older history. Local-branch fetch mode instead starts with incoming commits
after the current local tip, as described under Branch Selection. After review, the last
reviewed or explicitly skipped SHA is stored in `state.json`, and only newer commits are
processed.

Manual review accepts a short or full hexadecimal commit SHA. It writes a
report entry (updating it when the SHA already exists) and does not change the automatic monitoring position in
`state.json`. Stop automatic monitoring before starting a manual review.

To choose where automatic monitoring begins, enter a short or full SHA in
**Selected commit SHA**, then use **Start from selected commit** beside **Review
commit** on the Project tab. This immediately starts automatic review, including
the selected commit and then subsequent commits. The SHA must be an ancestor of the selected branch tip. It is
stored as a pending start, so the selected commit is reviewed first. The normal
branch cursor replaces it only after the review or exhausted-failure entry is saved.

Normal access validation and stop/error behavior apply. Automatic monitoring
continues afterward. The Dashboard has Start, Stop, Hide to tray and Exit in its
upper-right corner; Log and Open report are on the right of the commit card.
There is no bottom action bar or duplicate action beside the repository picker.

Journal updates tolerate hidden and not-yet-laid-out text fields, including the
separate details window. Scroll anchors are validated against the current layout.

Streaming model responses accept empty choice/delta trailers and final token usage
after `finish_reason`. Later generated content, tool calls, conflicting finish
reasons and missing `[DONE]` still fail the review rather than saving partial results.

### Native Git Agent

`ReviewRunner` validates the repository, fixes the reviewed full SHA, checks for
an empty change, and starts one `ModelClient` agent session. It does not capture
or supply a raw diff. The old `DiffChunker` flow has been removed. Chat requests
use OpenAI `tools`, `tool_choice: "auto"`, assistant `tool_calls`, and correlated
`role: "tool"` results across multiple turns. Multiple returned calls are executed
sequentially, even though parallel tool calls are disabled in requests.

| Tool | Scope |
| --- | --- |
| `git_metadata` | Metadata for the target or its immediate first parent; mentioned SHAs grant no additional access |
| `git_changed_files` | Paginated names/status against the target's first parent |
| `git_diff` | Paginated full target patch, root commits compared with the empty tree |
| `git_file` | Committed blob as paginated text/numbered line range, or original bytes with `format: "hex"` |
| `git_tree` | Paginated directory children at an allowed SHA; optional directory `path` and `recursive` (default false) |
| `git_search` | Paginated case-sensitive literal `query` matches; optional file/directory `path`; skips binary files |

Once tool reads are complete, a malformed final report gets up to two formatting
correction requests within the existing round/time budgets. Feedback identifies
invalid fields, missing markers and extra prose; the model must preserve its
findings and return the required plain-text blocks. Each rejected answer is saved
verbatim in a JSON diagnostic under `diagnostics/` in the application data folder,
with the reviewed SHA, model, attempt number and validation errors. If correction
fails, the error includes the diagnostic path. Automatic analysis retries apply;
once exhausted, the failure is recorded before skipping the commit.
Truncated responses and unfinished tool reads cannot be accepted through this retry.

Line ranges use 1-based inclusive bounds, at most 500 lines per range, and require
both bounds. Range conversion supports text blobs up to 512,000 characters;
larger blobs return a recoverable suggestion to use ordinary paginated `git_file`.
An end line past EOF is clipped; a start line past EOF is rejected. Search queries
are literal text, not regular expressions or Git flags. No matches is a successful
empty result. Tree/search output and numbered ranges are cached for stable paging
on disk and share the session disk budget with diffs. Keep the same path/query/range when
following `next_offset`.

For byte-level questions, `git_file` accepts `format: "hex"`, `byte_offset`
(default 0, nonnegative 32-bit position) and `byte_count` (default 256, maximum 4096).
The result includes uppercase `hex`, `blob_size_bytes`, `bytes_returned`,
`next_byte_offset` and `bom` (`UTF-8`, `UTF-16LE/BE`, `UTF-32LE/BE`, or `none`).
Bytes come directly from Git stdout's binary stream, without shell redirection,
text decoding, BOM removal or newline conversion. BOM is a signature, not validation
of the full encoding; `none` does not mean UTF-8. Byte offsets are independent from
text pagination. Combining hex with text `offset` or line bounds is rejected.
Reading at EOF returns an empty range; beyond EOF is rejected. Requests are bounded
and may seek to a specific byte range without reading prior pages through the model.

Refs such as `HEAD`, arbitrary revision expressions, paths outside the Git tree,
and user-supplied Git flags are not accepted by tools. Only the target and its
immediate first parent are allowed. `git_history` is no longer offered or executable.
Metadata cannot expand this allowlist: older ancestors and other merge parents
remain prohibited even when their SHAs appear in output. As with manual SHA input, repositories use
40-character SHA-1 object IDs; SHA-256 repositories are not supported.
Subjects are navigation hints, not evidence. Start with the reviewed diff and relevant
files at the reviewed SHA and its immediate first parent. Report confirmed issues
relevant to the change without investigating bug age, authorship or its originating
commit. Unrelated pre-existing bugs are outside the review. The mandatory protocol
overrides older/custom prompts that still request history exploration.

When `git_file` requests a path absent from an available commit tree, it returns
`status: "not_found"` with the requested SHA/path and suggestions to inspect the tree
at the target or first parent. This does not abort review or substitute working-copy contents.
It also applies to numbered-range and hex reads. The journal reports that investigation
continues. An existing tree entry with an unreadable/missing blob, unavailable commit,
damaged tree or timeout remains a failure, not an absent-path result.
Changing the selected branch or dirty checkout cannot
change the session's target. Renames are shown as deletion/addition, and merge
commits are reviewed against their first parent, not a combined merge diff.
Parent discovery reads raw commit headers, so a shallow boundary is not mistaken
for a root commit. A missing comparison parent fails locally without lazy fetching;
obtain the required history outside the model tools before retrying.

Git runs through `ProcessStartInfo.ArgumentList`, never a shell. Tools cannot
write, fetch, push, checkout, reset, run hooks, external diff, textconv, or network
protocols. Lazy fetching and replacement objects are disabled. File content comes
from `cat-file blob`, not the working directory; a symlink returns its stored link
text, never the target. Submodules are not traversed. Installed Git and local
repository administration remain trusted; this is not an OS sandbox against a
concurrent local attacker replacing repository metadata or the Git executable.

Every page is capped at 16,000 UTF-16 characters, with `offset` and `next_offset`
(`null` at EOF). Every paged resource must start at offset zero, then use exactly
its expected next offset; skipping unread content or seeking past EOF is rejected.
Previously read page offsets can be requested again, including offset zero after
EOF. Re-reading does not rewind progress or reopen a completed resource. Invalid
offset errors report the expected continuation offset to the model.
Output limits are enforced while draining the subprocess, including very long
lines, rather than truncating an unbounded captured string.

On the model's first request for diff, changed files, tree, search or a numbered
file range, the command output is streamed once into a temporary disk snapshot.
Snapshots share a **64 MiB per-session disk budget**, not a 512,000-character cap.
Set the environment variable `GITREVIEWER_SNAPSHOT_MB` to an integer from 1 to 1024
before launching the application to change this budget; invalid values use 64 MiB.
For example, in PowerShell: `$env:GITREVIEWER_SNAPSHOT_MB = '128'` before starting
the EXE from that shell. Storage uses two bytes per UTF-16 character. Temporary
files have delete-on-close handles; they are closed on completion, error or cancellation.
Failed/oversized captures are immediately discarded, never exposed as valid partial snapshots.
Auxiliary size limits return a recoverable tool result suggesting a narrower path,
query or range; they do not prevent a final report by themselves. A diff that
exceeds the disk budget still fails the review because full diff coverage is required.
Later pages use only the
snapshot, so changes to live attributes or rendering configuration cannot alter
or shorten the remaining pages. Rendering reflects local attributes/configuration
at capture time, not necessarily the attributes committed at the target SHA.
No diff is captured or supplied automatically by the runner. Committed blob pages
rerun the read-only object read. Offsets are nonnegative 32-bit character positions.
The model must consume the entire diff in order before a final report can be accepted.
Auxiliary tree/search/file/changed-file pages can be stopped after obtaining enough
relevant context. Tree browsing defaults to immediate children; scope requests by
directory rather than enumerating the entire repository. Binary diff markers are visible,
but binary semantics are not analyzed reliably; blob text uses UTF-8 decoding
with replacement for invalid bytes, not a binary download API.

Default budgets per review: 60 model rounds, 64 tool calls, 512,000 serialized tool-result
characters, and a 15-minute overall agent deadline. The Dashboard displays the
remaining agent time beside elapsed commit time; timeout errors explicitly name
the configured limit, while user cancellation remains cancellation. By default requests 41–60 include a fresh
budget reminder counting remaining model requests including the current request
(20 down to 1), with instructions to reserve a final report. Old reminders are not
accumulated in conversation history. Evidence/completeness requirements remain unchanged.
Each model response has independent
decoded-character budgets: 2,000,000 combined for `reasoning` + `reasoning_content`,
1,000,000 for `content`, and 256,000 for tool-call names/arguments and envelope
strings (IDs/types). Both SSE and non-streaming JSON enforce these budgets.
The raw HTTP/SSE transport limit is 128,000,000 characters including JSON overhead;
SSE is processed incrementally. Errors identify the exhausted category, received
count and limit. Git-result budgets and context-window limits are unchanged.
Each tool subprocess has a 30-second timeout and bounded stderr.
System prompts are capped at 32,000 characters. Cancellation kills Git process
trees and cancels HTTP work. Invalid arguments and unknown tools produce safe
error results for correction within the same budgets. Once arguments are valid,
a Git retrieval failure (including an unavailable blob, but not a confirmed absent path) is
fatal: reading an unrelated resource cannot clear a missing-context failure.
Malformed response
envelopes, unrecovered argument errors, model-output/budget exhaustion, unfinished diff pages,
non-`stop` final responses, or incomplete report blocks fail the analysis attempt.
After automatic retries, a failure entry is persisted and the commit is skipped,
without a success notification or email.
Large commits may therefore require a different workflow instead of being
silently reviewed only in part. The model-output budget is separate from snapshot
storage: increasing disk capacity does not increase model context or the 512,000
serialized tool-result character budget. Model budgets are configured per profile,
not inferred from model metadata.

### Model parameters

The **Models** tab contains an editable parameters table with permitted ranges.
Each visible row has a question-mark tooltip explaining its purpose, units and
effect in the selected UI language. Maximum output tokens and Git snapshot size
are not shown in the table; existing profile values are preserved when saving
other settings, and new profiles use their defaults.
Click **Save** to persist values in the profile's `parameters=` JSON entry in
`models.conf`. Existing profiles without this entry retain the defaults above.
Each review takes its own copy: editing a profile does not alter an active review.

| Parameter | Default |
|---|---:|
| Review time (minutes) | 15 |
| Automatic retries after failure | 3 (4 attempts including the first) |
| Temperature / Top P | 0 / 1 |
| Maximum output tokens (`max_tokens`) | 0: omit, use server default |
| Model request rounds | 60 |
| Start reminders when this many requests remain | 20 |
| Tool calls per review | 64 |
| Tool-result characters per review | 512000 |
| Reasoning / content / tool-argument characters per response | 2000000 / 1000000 / 256000 |
| Raw HTTP response characters | 128000000 |
| Git snapshot disk budget (MiB) | 0: environment or 64 MiB default |

Temperature, Top P and an optional maximum token count are sent to the server;
provider support and its context limit still apply. Reminders, protocol budget
text, timeout messages and the Dashboard timer reflect the configured values.
Internal safety limits (Git subprocess timeout, page sizes, allowed commits and
path restrictions) are not model-generation settings and remain unchanged.

After analysis retries for a commit are exhausted, its failure reason is saved in
the report and automatic review continues with the next commit. Zero retries skips
after the first failed analysis. Retries use the normal polling interval. A skipped
commit advances the cursor and, when applicable, the clean local branch, but is
never reported as bug-free and does not send a success email or tray notification.
Repository/fetch, metadata, persistence and unsafe branch-advancement errors still
stop monitoring after their retry budget: losing data or skipping an unknown Git
state is not safe. Cancellation never skips a commit. Manual failures are recorded
and shown to the user without automatic retries.

Empty changes are detected locally and reported honestly as not sent to the model.
Successful agent reviews retain the existing `BUG` / `NO_BUGS` report format.
Protocol/safety instructions are always added by the client regardless of the
embedded prompt. A single initial system message combines the embedded prompt,
then the mandatory overriding protocol and language instruction, for compatibility
with tool-capable Mistral/vLLM chat templates. Git content and branch context are explicitly untrusted data,
not commands or instructions. This reduces prompt-injection risk but cannot
guarantee that a model's findings are accurate.

## Journal And Log

The former Log tab is now **Journal** (**Журнал**) and retains general messages.
Journal is appended to `%LOCALAPPDATA%\GitReviewer\journal.log`; the last 500
lines are loaded into the tab after a complete application restart. The file
rotates at 8 MiB and one previous generation is retained as `journal.log.1`.

The main window's **Log** (**Лог**) button opens a separate detailed model window.
It records the complete JSON request body, raw HTTP response stream, assistant
content, server-returned `reasoning` / `reasoning_content`, tool-call arguments,
tool results, and lifecycle stages. Streaming responses appear while generation
is in progress. Hidden chain-of-thought cannot be recovered when a server or model
does not return those fields. Authorization headers, API keys, and environment
variable values are never logged.

Detailed data is appended to `%LOCALAPPDATA%\GitReviewer\model-log.log`; it rotates
at 32 MiB and keeps one previous generation as `model-log.log.1`. The Log window
shows a bounded recent tail to keep WPF responsive, while the files hold the full
retained exchange. These files contain prompts, repository paths, diffs, committed
file contents, and model responses and must therefore be treated as sensitive.
Closing Log does not interrupt review; reopening restores its recent tail. Hiding
the main window also hides Log, and exiting the app closes it.

After the report is written (and the automatic cursor saved), the app requests
one tray balloon per completed commit, including `NO_BUGS`. Historical unstructured
reports remain readable; new incomplete/unstructured agent replies are retried and,
if exhausted, recorded as failed before advancing the cursor. Empty diffs are marked as
not sent to the model, rather than claiming no bugs. Failed or canceled reviews
do not generate completion notifications. Notification failures do not affect
review state. Windows notification settings may suppress or coalesce balloons.

If cursor saving fails or is canceled after report writing, the next automatic
attempt may receive a different model result. It atomically replaces that commit's
report entry before saving the cursor and publishing completion, so
the notification matches the persisted outcome without duplicate entries.
Automatic and manual reviews of the same SHA update one entry with the latest
outcome; legacy duplicate entries for that SHA are consolidated. Other commits are
preserved. **Clear report** on the Dashboard clears only the selected repository and
branch, with confirmation and a `.cleared-*.bak` backup alongside the report.
It does not reset the review cursor or modify queued emails. Running reviews may
write new entries after clearing.

The model returns plain text blocks:

```text
BUG
FILE: calculator.py
LINE: 12
SIDE: OLD
DESCRIPTION: The division-by-zero validation was removed.
END
```

If no bugs are found, the expected response is:

```text
NO_BUGS
```

## Model Profiles

Only the safe template `models.example.conf` is stored in Git. Actual profiles
and API keys are saved locally in `models.conf`.

An environment variable can be used instead of storing an API key in the file.
When both are configured, `api_key_environment` takes precedence over
`api_key`.

Model endpoints may use HTTP or HTTPS. HTTP is useful for local networks and
self-hosted model servers, but it does not encrypt the API key or repository
content requested through tools. The GUI displays a warning for non-loopback HTTP endpoints.

Review requires native OpenAI-compatible function calling, not merely chat text
or JSON mode. The chosen model, chat template, and server must support `tools`,
`tool_choice: "auto"`, `tool_calls`, tool result messages and `finish_reason`.
For vLLM, configure `--enable-auto-tool-choice` and `--tool-call-parser` with the
parser appropriate to the served model; consult that model's vLLM instructions.
Ollama/LM Studio support likewise depends on the model and server version.
Provider rejections include actionable compatibility guidance and never silently
fall back to the old flow. **Test connection** remains a simple chat check: success
does not verify tool calling or adequate context capacity for a review.

### vLLM And Model Discovery

In **Models**, enter your server endpoint, then click **Load models**. No profile
name or model is required for discovery. Select the exact served model ID from
the editable dropdown, or type it manually if discovery is unavailable. Loading
models never replaces your current model name. Click **Test connection** to send
a small chat request, then **Save** to persist the profile.

Supported endpoint forms (including reverse-proxy path prefixes):

| Entered path | Chat POST path | Discovery GET path |
| --- | --- | --- |
| `/v1` or `/v1/` | `/v1/chat/completions` | `/v1/models` |
| `/v1/models` | `/v1/chat/completions` | `/v1/models` |
| `/v1/chat/completions` | unchanged | `/v1/models` |

Query parameters are retained. Other full endpoints remain unchanged for chat
requests; discovery can also replace a final `/chat/completions` with `/models`.
For custom endpoints without a recognized suffix, enter the model manually.
Use the explicit `/v1` base rather than a bare server hostname. Stored endpoint
strings and the existing profile file format are not rewritten or migrated.

Both discovery and chat use optional Bearer authentication: a nonblank value
from the configured environment variable takes precedence, otherwise the stored
API key is used. Leave both blank for a server that does not require authentication.
No server address or model ID is assumed.

If vLLM reports `max_model_len`, the selected model's context limit is displayed
as information only. It is not saved, sent in chat requests, or used to change
agent paging or output limits. Missing metadata does not prevent model selection.
Editing connection details or switching profiles clears discovered metadata;
responses from requests started before form edits or newer operations are ignored.

### Focused Checks

The separate `GitReviewer.Tests` repository links production services and
uses a fake HTTP handler, isolated data paths, and temporary Git repositories.
Its extended Git transport fixture runs on Linux using a local SSH shim; no
server or credentials are required:

```sh
dotnet run --project ../GitReviewer.Tests/GitReviewer.Tests.csproj
dotnet run --project ../GitReviewer.Tests/PromptMigration/PromptMigration.csproj
```

Clone or place the test repository as `GitReviewer.Tests` next to `GitReviewer`,
then run these commands from `GitReviewer`. Tests cover linked worktrees and their
shared object store, case-sensitive refs, custom
remote mappings, dirty checkout preservation, cursor migration, manual and
automatic completion, native multi-turn/multi-call tool flow, paged output,
unknown tools, malformed arguments/responses, incomplete finals, budgets,
failures, and cancellation. Real Git fixtures check immutable SHA reads, root
commits, dirty files, unsafe paths, binary changes and symlink blobs. Plink checks
cover path persistence, shell-safe custom paths, blank-path detection, missing
paths, and background fetch. In-flight manual and automatic model requests are
also checked for clean cancellation without completion notifications. This local
test project is ignored and is not included in the production distribution.
The second command checks embedded EN/RU prompt loading and verifies that legacy
prompt files are ignored and preserved, including read-only files.
The WPF project can be built on Linux with `dotnet build`, but running and
interactively checking the GUI and tray notifications requires Windows.

## User Data

### SMTP notifications

The **Mail** tab configures the SMTP host/port, `None`, required `StartTls` or
`SslOnConnect`, optional username/password, sender and semicolon-separated
recipients. Sender display names use `GitReviewer <sender@example.com>` syntax.
Notifications are disabled by default. **Send test email** saves validated settings
before attempting delivery (also when delivery fails), without enabling notifications,
and sends only synthetic text, without repository data. It requires an explicit
recipient. `None` transmits credentials and reports
unencrypted and asks for confirmation; use it only on a trusted test network.
TLS modes use normal certificate validation. OAuth is not implemented.

After a successful saved review containing findings, a background service queues
one message with that commit's report rendered as HTML in the body and attached as
`commit-review.html`, with a plain-text fallback. Repository/model text is HTML-escaped;
scripts, remote images and raw HTML are not executed. Existing queued Markdown reports
are converted at delivery time. Local reports remain Markdown.
It does not attach the accumulated report history.
**Send even without bugs** also enables notifications for successful NO_BUGS and
empty-diff reviews. Incomplete/unstructured reviews and timeouts never trigger mail.
**Send to commit author** adds the Git author email, deduplicated against configured
recipients. Invalid author addresses are skipped; with no valid recipients the
notification is skipped. Author addresses are untrusted repository data: enable
this option only for trusted repositories, since reports may leave your organization.
Both options are disabled by default and do not resend previous reviews. SMTP failures
never repeat or invalidate the Git/model review.

`mail-settings.json` stores credentials encrypted with Windows DPAPI for the
current user. `mail-queue.json` preserves pending reports and delivery identifiers
across restarts; queued reports contain repository information and should be
protected like report files. Successful delivery removes the stored report body.
Automatic retries run after 1, 5 and 15 minutes (four attempts total), with a
30-second SMTP operation timeout. **Retry failed mail** requeues exhausted entries.
Disabling mail pauses queued sends; an already sending message may still complete.
Queued recipients are fixed at enqueue time, even if settings subsequently change.

Deduplication uses repository identity, branch and commit, including manual reviews.
A stable Message-ID is reused for retries. SMTP cannot guarantee exactly-once
delivery: loss of the server's final confirmation or a crash before saving delivery
state can still produce a duplicate. Queue/settings write failures are logged;
corrupt files are preserved rather than silently overwritten. SMTP protocol traces
and passwords are not written to logs.
The journal records why a notification is skipped (disabled, no findings, incomplete,
or already queued/sent), and identifies queued/accepted messages by commit. A journal
callback failure cannot terminate the mail worker. SMTP acceptance does not guarantee
inbox delivery; repeated reviews of an already mailed SHA are still deduplicated.

Run isolated queue/credential tests using
`dotnet run --project GitReviewer.Tests/Mail/Mail.csproj`. They do not send mail.

New journal and detailed-log entries use local computer time in
`yyyy-MM-dd HH:mm:ss` format, without an offset suffix. Existing log history is
left unchanged. Reports include analysis duration (`HH:mm:ss.fff`) for each
successful commit review, measured with a monotonic clock from commit preparation
through model analysis and parsing. Fetch, retry waiting, and report writing are
excluded; each attempt has its own duration.

User-specific files are stored in:

```text
%LOCALAPPDATA%\GitReviewer
```

For isolated or portable runs, set `GITREVIEWER_DATA_DIR` before starting the
application to use a different data directory.

The directory contains:

```text
models.conf          Model profiles and optional API keys
settings.conf        Repository, branch ref, authentication, interval, fetch, language
state.json           Last reviewed commit for each repository and branch
reports\             Markdown review reports
```

These files are not committed to Git.

## Submodules

Review understands Git `160000` gitlink entries, including additions, deletions,
pointer updates across several commits, and nested changes (up to 8 levels).
The main `git_diff` response lists changed submodules with their exact old/new SHAs.
The agent must read every listed module's complete diff using the `submodule`
argument, including nested entries. A parent-only diff cannot produce a successful
final review. Findings inside modules use full paths such as `lib/src/file.cs`.

All six Git tools accept `submodule`; file/tree/search paths are then relative to
that module. Only the two pinned gitlink commits are allowed, not their arbitrary
ancestors. A pointer update is compared across its entire old-to-new range; adding
or deleting a module compares against the empty tree. Working-copy contents are
never used as review evidence. Child snapshots share the main session's disk budget.

Modules must be initialized locally at their repository paths. Missing directories,
missing pinned objects, symlink/junction module paths or excessive nesting produce
an incomplete review, not a clean result. Renamed/deleted modules must still have
their local repositories available at the historical paths to inspect removed code.
Only changed modules are exposed to the model in this version.

**Fetch initialized submodules on demand** is a per-project opt-in, disabled by
default. With the main fetch checkbox enabled it uses Git's recursive on-demand
fetch for initialized modules; the existing Git transfer log and timeout apply.
It can contact different configured remotes using the selected credentials: enable
it only for trusted repositories. It never automatically initializes/clones a new
module. With fetch disabled, review is local-only. Missing historical objects may
still require fetching the affected module manually.

After a clean local branch advances, initialized modules are checked out to their
pinned commits recursively, with `--no-fetch --checkout`, no force and hooks/network
disabled. Dirty modules still block advancement; changes are never stashed or
discarded. If module checkout fails after the parent fast-forward, the error is
shown and monitoring must resolve the working-copy state before continuing.

For a trusted repository, initialize modules yourself with
`git submodule update --init --recursive` before review. This command may download
repositories and change their working files; check local changes and URLs first.

## Multiple projects

The bottom navigation is shared by all tabs: numbered buttons select saved projects,
**+** adds a project and **−** removes it from the list after confirmation. Hover a
number to see its repository and branch. Removing an entry never deletes repository
files, reports, queued mail or review progress.

Each project stores its repository, branch, polling interval, fetch flag and Git
authentication settings. Changes are saved when switching or exiting. The selected
project survives restart. On first launch, `settings.conf` is imported as project 1
into an atomically saved `projects.json`; the legacy file is left unchanged.
Corrupt or unsupported project catalogs are not silently overwritten.

One project is reviewed at a time. Switching a running automatic review requires
confirmation and awaits its stop; a manual review must finish before switching.
The new selection does not start automatically. Review positions and reports remain
keyed by repository Git identity and branch, so two entries for the same repository
and branch share progress. Model profiles, SMTP settings, prompts, language, the
journal and daily/all-time token totals are application-wide.

Run migration checks with `dotnet run --project GitReviewer.Tests/PromptMigration/PromptMigration.csproj`
and isolated UI switching/layout checks with `dotnet run --project GitReviewer.Tests/ProjectsUi/ProjectsUi.csproj`.

## Language

Select English or Russian on the **Project** tab. The choice is saved in
`settings.conf` and applies to the GUI, tray menu, log messages, reports, and
model instructions.

System prompts are embedded in the executable from `GitReviewer/system-prompt.example.txt`
and `GitReviewer/system-prompt.ru.example.txt`. Updating the application therefore
updates the default prompt automatically. The **System prompt** tab allows editing
and explicitly saving a custom prompt (1–32000 characters). Overrides are stored
separately for each language in `system-prompt.custom.en.txt` and
`system-prompt.custom.ru.txt` in the data directory. A saved override takes priority
over the embedded default, including after application updates. **Reset to default**
removes the current language's override after confirmation. Changes apply to the
next review, not a running model request. Legacy `system-prompt.txt` files in AppData
remain ignored and are never rewritten or deleted.
