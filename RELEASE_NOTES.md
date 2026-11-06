# Usage Overlay 0.5.0 — danscMax fork

First compact workspace-status fork release, based on upstream 0.4.4.

## Highlights

- Horizontal bar and vertical dock: context used/free, quotas, reset countdowns, content-driven sizing, free-side placement and tiny layout switch.
- Git ahead/behind, changed files, branches, stashes/worktrees and optional PR/review information from an existing GitHub CLI sign-in.
- Codex tokens, cache, speed, task duration, action, child agents, queue and separate OS child-process count. Missing sources remain `—`.
- Existing environment diagnostics, statusline-style icons/colours, Russian/English UI and both themes. Settings on the main bar; clicking disclosure closes hover-open details without immediate reopening.
- Slim vertical width measured from its contents, without a duplicate free percentage or middle-dot separators. Hover collapse checks physical pointer coordinates at fractional DPI instead of relying on a transient mouse-leave event during native resizing.
- Preserve the Codex host while the overlay has focus, avoiding hide/show/reopen cycles when using its controls. A minimized, closed or disabled host is still rejected.
- Original author moved to Acknowledgements. MIT license retained unchanged; component notices included. Manual updates now target this fork, including the normal no-published-release state.

## Upgrade

Close an existing overlay before installing. This build keeps the original install identity and shares `%USERPROFILE%\.usage-overlay\settings.json`; replace upstream rather than running both. Back up settings before switching versions. Existing preferences and integration opt-ins are preserved. Language changes require restarting.

Choose the per-user installer or complete portable ZIP. Verify `SHA256SUMS.txt`. The runtime is bundled. Windows 10/11 x64; binaries are unsigned.

## Known limits

Extended session indicators target Codex and depend on internal local formats. Git ahead/behind uses local remote-tracking refs without fetching. `gh` is optional. Diagnostics use existing fresh caches/provider errors, without new infrastructure probes. Optional installation reporting remains off by default and uses the inherited original endpoint if enabled.

Maintained independently at https://github.com/danscMax/usage-overlay. Original and component credits/licenses are included in both packages.
