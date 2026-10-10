# Code signing policy

Fastboot Studio's Windows releases are set up to be signed through
[SignPath.io](https://about.signpath.io), with a certificate from the
[SignPath Foundation](https://signpath.org). Until the project is accepted, releases are
unsigned.

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

## What is signed

- `FastbootStudio.exe`, built from this repository by GitHub Actions
  ([release.yml](.github/workflows/release.yml)) from a tagged commit. No build is signed
  from a developer's machine.
- Not signed by this project: `adb.exe`, `fastboot.exe`, `AdbWinApi.dll` and
  `AdbWinUsbApi.dll`. They are Google's Android SDK Platform-Tools, included unchanged
  (see `platform-tools-NOTICE.txt` in the release zip).

## Team

| Role | Members |
| --- | --- |
| Committers and reviewers | [IFOKNR](https://github.com/ifoknr) |
| Approvers | [IFOKNR](https://github.com/ifoknr) |

Every signing request is approved by an approver. Changes from outside contributors are
merged through pull requests reviewed by a committer. Members use multi-factor authentication
on GitHub and SignPath.

## Privacy

Fastboot Studio does not send any information to other networked systems. It talks only to
the phone connected over USB (through adb and fastboot), and opens web pages in your browser
only when you click a link in the app.

## Reporting a problem

If a signed file looks wrong or you think the certificate is misused, open an issue at
https://github.com/ifoknr/FastbootStudio/issues or write to Telegram
[@IFOKNR1](https://t.me/IFOKNR1).
