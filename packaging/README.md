# Packaging · النشر

## winget

`.github/scripts/winget-manifests.sh` writes the manifests of a release (version, installer,
English and Arabic descriptions). The Release workflow runs it for every release and attaches
the result as the `winget-manifests` artifact of the run. They follow schema 1.6.0: a portable
zip whose folder goes on PATH (`ArchiveBinariesDependOnPath`), because FastbootStudio.exe needs
the adb and fastboot next to it.

**First submission** (once): copy `manifests/i/IFOKNR/FastbootStudio/<version>/` into a fork of
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) at the same path and open a
pull request; agree to the Microsoft CLA when the bot asks. Or, on Windows:
`wingetcreate submit <folder>`.

**Later releases**: add a classic GitHub token with the `public_repo` scope as the
`WINGET_TOKEN` secret; the Release workflow then submits each new version by itself.

## Code signing

Release builds can be signed through SignPath (free for open source); the workflow is ready and
switches on once the SignPath secret and variable exist. Steps: [`signing/README.md`](signing/README.md).
Policy: [`CODE_SIGNING.md`](../CODE_SIGNING.md).

## XDA

`xda/thread.bbcode` is the thread for XDA Developers (BBCode, paste it in the editor's BBCode
mode). The images come straight from `docs/` on GitHub, so they stay current.
