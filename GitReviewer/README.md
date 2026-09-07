# Git Reviewer

Git Reviewer is a Windows desktop application that uses a local or cloud
OpenAI-compatible model to inspect Git commits for correctness bugs.

## Features

- Reviews the current `HEAD` on the first connection without scanning older commits.
- Runs `git pull --ff-only` and reviews each newly received commit separately.
- Supports local-only repositories with automatic pull disabled.
- Supports private remotes through SSH Agent, an SSH private key file, or HTTPS credentials.
- Reviews added, modified, and deleted lines from each commit diff.
- Allows manual review of any commit by its short or full SHA.
- Supports multiple model profiles for OpenAI-compatible APIs, Ollama, and LM Studio.
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

For a framework-dependent Windows x64 publication:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

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

The **Project** tab provides three authentication modes:

- **SSH Agent** uses keys already loaded into Windows OpenSSH Agent. This is the
  recommended mode for private keys protected by a passphrase.
- **SSH Key File** passes the selected private key file to OpenSSH. Only the
  path is stored in `settings.conf`; the key is not copied. Add the matching
  public key to the Git server. Use SSH Agent for an encrypted key.
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
SSH connections run non-interactively and require the server to already exist
in the user's `known_hosts` file. The application never stores an SSH
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

Remote endpoints must use HTTPS. HTTP is allowed for loopback endpoints used by
local model servers.

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
