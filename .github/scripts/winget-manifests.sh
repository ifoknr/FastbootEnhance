#!/usr/bin/env bash
# Writes the winget manifests of a release: version, installer, and the English and
# Arabic descriptions. The zip is a portable app; winget unpacks it and puts its folder
# on PATH (ArchiveBinariesDependOnPath), since FastbootStudio.exe needs the adb and
# fastboot next to it.
#
# Usage: winget-manifests.sh v2.2.0 path/to/FastbootStudio-v2.2.0-win-x64.zip OUTDIR [YYYY-MM-DD]
set -euo pipefail

tag="$1"
zip="$2"
out="$3"
date="${4:-$(date -u +%Y-%m-%d)}"
version="${tag#v}"
id="IFOKNR.FastbootStudio"
repo="https://github.com/ifoknr/FastbootStudio"
sha=$(sha256sum "$zip" | cut -d' ' -f1 | tr 'a-f' 'A-F')
url="$repo/releases/download/$tag/$(basename "$zip")"
dir="$out/manifests/i/IFOKNR/FastbootStudio/$version"
mkdir -p "$dir"

cat > "$dir/$id.yaml" <<YAML
# yaml-language-server: \$schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
YAML

cat > "$dir/$id.installer.yaml" <<YAML
# yaml-language-server: \$schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $version
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
- RelativeFilePath: FastbootStudio.exe
  PortableCommandAlias: fastbootstudio
ArchiveBinariesDependOnPath: true
MinimumOSVersion: 10.0.17763.0
ReleaseDate: $date
Installers:
- Architecture: x64
  InstallerUrl: $url
  InstallerSha256: $sha
ManifestType: installer
ManifestVersion: 1.6.0
YAML

cat > "$dir/$id.locale.en-US.yaml" <<YAML
# yaml-language-server: \$schema=https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $version
PackageLocale: en-US
Publisher: IFOKNR
PublisherUrl: https://github.com/ifoknr
PublisherSupportUrl: $repo/issues
Author: IFOKNR
PackageName: Fastboot Studio
PackageUrl: $repo
License: MIT
LicenseUrl: $repo/blob/master/LICENSE
Copyright: Copyright (c) 2021 LibXZR, (c) 2026 IFOKNR
ShortDescription: The Android toolbox for Windows - fastboot, OTA flashing, payload dumper, super images and backups.
Description: |-
  Fastboot Studio is a modern rewrite of Fastboot Enhance. It flashes full OTAs with every
  image checked before anything is written, dumps payload.bin, converts and unpacks sparse
  and super images, builds super images like lpmake, backs up partitions and files over adb,
  and guards risky actions: slot switches, critical partitions and room in super.
  adb and fastboot are included; nothing else to install.
Moniker: fastbootstudio
Tags:
- android
- adb
- fastboot
- flashing
- payload-dumper
- super-image
- rom
- backup
ReleaseNotesUrl: $repo/releases/tag/$tag
ManifestType: defaultLocale
ManifestVersion: 1.6.0
YAML

cat > "$dir/$id.locale.ar-SA.yaml" <<YAML
# yaml-language-server: \$schema=https://aka.ms/winget-manifest.locale.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $version
PackageLocale: ar-SA
Publisher: IFOKNR
PackageName: Fastboot Studio
License: MIT
ShortDescription: أدوات أندرويد لويندوز - فاست بوت، تفليش التحديثات، مستخرج Payload، صور super والنسخ الاحتياطي.
Description: |-
  نسخة جديدة من Fastboot Enhance. يفلّش التحديثات الكاملة بعد التحقق من كل صورة، ويستخرج
  payload.bin، ويحوّل ويفك صور sparse و super، ويبني super مثل lpmake، وينسخ الأقسام والملفات
  احتياطياً عبر adb، ويحمي من الأخطاء الخطرة. adb و fastboot مدمجة، بدون تثبيت أي شيء ثاني.
ReleaseNotesUrl: $repo/releases/tag/$tag
ManifestType: locale
ManifestVersion: 1.6.0
YAML

echo "$dir"
ls "$dir"
