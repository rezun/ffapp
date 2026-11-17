Always read `docs/coding-style.md`

Commit on main by default.

Do not open, focus, or interact with desktop app windows for verification unless the user explicitly requests it. Use headless checks and leave interactive end-to-end testing to the user.

For every user-visible change, add a short bullet under `## [Unreleased]` in `CHANGELOG.md` (headings `Added`, `Changed`, `Fixed`, `Removed`) in the same commit. Skip purely internal changes such as tests, refactoring or CI. To release, run `python3 scripts/release.py prepare <version>`; the release workflow publishes that version's section as the GitHub release notes and fails if it is missing.
