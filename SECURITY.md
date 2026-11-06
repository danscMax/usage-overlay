# Security policy

## Supported versions

This fork is a preview. Security fixes target the latest fork release and the default branch at `danscMax/usage-overlay`.

## Report a vulnerability

Do not open a public issue for a suspected vulnerability. Use the repository's private security advisory feature and include:

- A concise description of the issue
- Reproduction steps or a minimal proof of concept
- Affected version and Windows version
- Expected and observed behavior
- The security impact you believe is possible

Do not include real credentials, session tokens, private conversations, or another person's data.

## Trust boundaries

Usage Overlay:

- Starts the locally installed Codex CLI in App Server stdio mode
- Requests account rate-limit metadata through Codex App Server
- Uses Codex-owned authentication without reading authentication files
- Identifies the active Codex window through Windows process metadata
- Reads local Codex rollouts, read-only state/queue databases and active-thread IPC for session indicators
- Reads Git and existing diagnostics caches; optional PR/review requests use an existing GitHub CLI sign-in
- Stores settings at `%USERPROFILE%\.usage-overlay\settings.json` and redacted logs under `%LOCALAPPDATA%\UsageOverlay`
- Creates Start-menu or Startup shortcuts only when the user requests them

It does not inject code into Codex or upload local session/workspace data. Session readers parse local rollouts for token/tool state; these files may also contain conversation text, so access is not quota-only. Optional saved-sign-in integrations have separate credential access described in `docs/privacy.md`. Optional installation reporting is off by default; it sends only a random ID and app version to the original Haroone endpoint over HTTPS, without cookies/redirects, with a five-second timeout and at most one attempt per UTC day. Manual updates use this fork's GitHub Releases API.

## Local attack considerations

The overlay executes the `codex` command found on `PATH`, unless `USAGE_OVERLAY_CODEX_PATH` points to a specific executable. The legacy `QUOTARAIL_CODEX_PATH` and `CODEX_USAGE_CODEX_PATH` variables remain supported for backward compatibility. Users should install Codex CLI from a trusted source and keep untrusted directories out of `PATH`.

Diagnostic messages are redacted and truncated before writing. Review logs before sharing them because error messages can still contain machine or environment details.

Release binaries should be downloaded from this repository's official Releases page and verified against the published SHA-256 checksum.
