# Git Reviewer

Git Reviewer is a Windows desktop application that uses a local or cloud
OpenAI-compatible model to inspect Git commits for correctness bugs.

## Features

- Reviews the current `HEAD` on the first connection without scanning older commits.
- Runs `git pull --ff-only` and reviews each newly received commit separately.
- Supports local-only repositories with automatic pull disabled.
- Supports private remotes through SSH Agent, OpenSSH keys, PuTTY `.ppk` keys, or HTTPS credentials.
- Reviews added, modified, and deleted lines from each commit diff.
- Allows manual review of any commit by its short or full SHA.
- Supports multiple model profiles for OpenAI-compatible APIs, vLLM, Ollama, and LM Studio.
- Uses an editable system prompt and a simple text response format instead of model-generated JSON.
- Provides English and Russian user interfaces and prompts.
- Continues monitoring in the Windows system tray after the main window is closed.
- Writes findings to a Markdown report with commit, file, and line information.

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
dist\GitReviewer-1.2.0-win-x64.exe
```

The executable includes the .NET runtime and default configuration templates.
It can be moved and launched by itself; no adjacent DLL or template files are
required.

After launch, select a Git repository, configure a model profile, test the
connection, and click **Start**.

Closing or minimizing the window hides it in the system tray. Monitoring keeps
running in the background. Use **Exit** from the tray menu to stop the process.

## Git Pull Behavior

When automatic pull is enabled, the application runs this command in the
selected repository before each check:

```powershell
git pull --ff-only
```

The selected branch must track a remote branch. `--ff-only` downloads linear
updates without allowing the application to create merge commits. Disable this
option for a repository that exists only locally.

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
  selected `.ppk` file. Install PuTTY in its standard location or add it to
  `PATH`. Load an encrypted key into Pageant before starting the review.
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

The upstream remote is read from the current branch, so the test uses the same
remote as `git pull`. SSH Agent mode explicitly uses Windows OpenSSH Client and
the Windows `ssh-agent` service instead of Git for Windows' bundled SSH client.
SSH connections run non-interactively. OpenSSH requires the server to already
exist in the user's `known_hosts` file; PuTTY uses its own host-key cache.
Verify and accept the server fingerprint using the corresponding client before
running unattended checks. The application never stores an SSH
passphrase or an HTTPS token itself.

## Review Behavior

On the first connection, only the current `HEAD` is reviewed against its first
parent. Earlier commits are not sent to the model. After that, the last
successfully reviewed SHA is stored in `state.json`, and only newer commits are
processed.

Manual review accepts a short or full hexadecimal commit SHA. It writes a
separate report entry and does not change the automatic monitoring position in
`state.json`. Stop automatic monitoring before starting a manual review.

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
diff. The GUI displays a warning for non-loopback HTTP endpoints.

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
diff chunking or output limits. Missing metadata does not prevent model selection.
Editing connection details or switching profiles clears discovered metadata;
responses from requests started before form edits or newer operations are ignored.

### Cross-Platform Client Checks

The dependency-free .NET 10 test executable links the production client source
and uses a fake HTTP handler (no server or credentials required):

```sh
dotnet run --project ../GitReviewer.Tests/GitReviewer.Tests.csproj
```

Run this command from `GitReviewer`. The WPF project can be built on Linux with
`dotnet build`, but running and interactively checking the GUI requires Windows.

## User Data

User-specific files are stored in:

```text
%LOCALAPPDATA%\GitReviewer
```

The directory contains:

```text
models.conf          Model profiles and optional API keys
settings.conf        Repository, authentication, interval, pull, and language
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
