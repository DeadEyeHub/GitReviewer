# Git Reviewer

Version **2.1.14** uses native model-driven Git tools and requires a
tool-capable model/provider. There is no legacy diff-prompt fallback.

Git Reviewer is a Windows desktop application that uses a local or cloud
OpenAI-compatible model to inspect Git commits for correctness bugs.

## Features

- Keeps separate repository and branch cursors in `state.json`; selecting a valid new repository registers it without reusing another repository's cursor.
- Reviews the selected branch tip on first connection without scanning older commits, except in local-branch fetch mode, which starts with incoming commits after the local tip.
- Fetches remote updates; a selected checked-out local branch advances by fast-forward after each successful commit review, only with a clean working copy.
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
- Uses an editable system prompt and a simple text response format instead of model-generated JSON.
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

The bottom status area shows the current commit SHA and subject. Long subjects
are ellipsized; hover to see the full title. A new review clears the previous title
before loading commit metadata.

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
dist\GitReviewer-2.1.14-win-x64.exe
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
working files advance to that commit using `merge --ff-only`. A failed review
does not advance the branch. The local tip remains authoritative on restart,
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
successfully reviewed SHA is stored in `state.json`, and only newer commits are
processed.

Manual review accepts a short or full hexadecimal commit SHA. It writes a
separate report entry and does not change the automatic monitoring position in
`state.json`. Stop automatic monitoring before starting a manual review.

To choose where automatic monitoring begins, enter a short or full SHA in
**Selected commit SHA**, then use **Start from selected commit** next to the
current commit. The SHA must be an ancestor of the selected branch tip. It is
stored as a pending start, so the selected commit is reviewed first. The normal
branch cursor replaces it only after that review succeeds.

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
fails, the error includes the diagnostic path and the review cursor is not advanced.
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

Budgets per review: 32 model rounds, 64 tool calls, 512,000 serialized tool-result
characters, and a 10-minute overall agent deadline. Each model response has independent
decoded-character budgets: 2,000,000 combined for `reasoning` + `reasoning_content`,
1,000,000 for `content`, and 256,000 for tool-call names/arguments and envelope
strings (IDs/types). Both SSE and non-streaming JSON enforce these budgets.
The raw HTTP/SSE transport limit is 128,000,000 characters including JSON overhead;
SSE is processed incrementally. Errors identify the exhausted category, received
count and limit. Git-result budgets and context-window limits are unchanged.
Each tool subprocess has a 30-second timeout and bounded stderr.
Custom prompts are capped at 32,000 characters. Cancellation kills Git process
trees and cancels HTTP work. Invalid arguments and unknown tools produce safe
error results for correction within the same budgets. Once arguments are valid,
a Git retrieval failure (including an unavailable blob, but not a confirmed absent path) is
fatal: reading an unrelated resource cannot clear a missing-context failure.
Malformed response
envelopes, unrecovered argument errors, model-output/budget exhaustion, unfinished diff pages,
non-`stop` final responses, or incomplete report blocks fail the review. No report,
cursor advancement, or completion notification is produced for those failures.
Large commits may therefore require a different workflow instead of being
silently reviewed only in part. The model-output budget is separate from snapshot
storage: increasing disk capacity does not increase model context or the 512,000
serialized tool-result character budget. Model budgets are fixed, not inferred from model metadata.

Empty changes are detected locally and reported honestly as not sent to the model.
Successful agent reviews retain the existing `BUG` / `NO_BUGS` report format.
Protocol/safety instructions are always added by the client regardless of the
editable prompt. A single initial system message combines the custom prompt,
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
reports remain readable, but new incomplete/unstructured agent replies fail rather
than advancing the cursor. Empty diffs are marked as
not sent to the model, rather than claiming no bugs. Failed or canceled reviews
do not generate completion notifications. Notification failures do not affect
review state. Windows notification settings may suppress or coalesce balloons.

If cursor saving fails or is canceled after report writing, the next automatic
attempt may receive a different model result. It atomically replaces that commit's
automatic report entry before saving the cursor and publishing completion, so
the notification matches the persisted outcome without duplicate entries.
Other commits and manual review entries are preserved.

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
The second command checks production prompt seeding, exact legacy EN/RU migration,
and preservation of customized prompts in an isolated local data directory.
The WPF project can be built on Linux with `dotnet build`, but running and
interactively checking the GUI and tray notifications requires Windows.

## User Data

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
system-prompt.txt    Editable system prompt
state.json           Last reviewed commit for each repository and branch
reports\             Markdown review reports
```

These files are not committed to Git.

## Language

Select English or Russian on the **Project** tab. The choice is saved in
`settings.conf` and applies to the GUI, tray menu, log messages, reports, and
model instructions.

If the system prompt still matches one of the default templates, switching the
language switches the prompt automatically. A customized prompt is never
overwritten by language switching.

On upgrade, exact shipped pre-2.0 English/Russian default prompts are migrated to
the new tool-aware defaults. Customized persisted prompts are preserved. The
client's mandatory protocol explains that older references to a supplied diff
now mean the diff retrieved through tools; users can restore defaults explicitly.
