# Fastboot Enhance

![A user-friendly **Fastboot ToolBox** & **Payload Dumper** for Windows](screenshots/Banner.png)

<img src="screenshots/ss1.png" width="400" height="300" /> <img src="screenshots/ss2.png" width="400" height="300" />
<img src="screenshots/ss3.png" width="400" height="300" /> <img src="screenshots/ss4.png" width="400" height="300" />

## What can it do?

- Show fastboot vars
- Switch between fastbootd, bootloader, recovery & system
- Switch between A & B slot
- **Flash Payload.bin in fastbootd**
- Flash images
- Erase partitions
- Delete logical partitions
- Create logical partitions
- Resize logical partitions
- Unpack Payload.bin
- **Extract specific image from Payload.bin**
- Show Payload vars
- Show dynamic partition metadata

## Supported payload formats

Every compression format a current A/B OTA can use is handled, in managed code,
with no native decompression libraries shipped alongside the app:

| Operation | Type | Status |
| --- | --- | --- |
| `REPLACE` | 0 | supported (raw) |
| `REPLACE_BZ` | 1 | supported (bzip2) |
| `REPLACE_XZ` | 8 | supported (xz) |
| `ZSTD` | 14 | supported (zstd) |
| `ZERO` | 6 | supported |
| `DISCARD` | 7 | supported |
| `SOURCE_COPY`, `MOVE` | 4, 2 | reported as needing the previous image |
| `BSDIFF`, `SOURCE_BSDIFF`, `BROTLI_BSDIFF`, `PUFFDIFF`, `ZUCCHINI`, `LZ4DIFF_*` | 3, 5, 10, 9, 11, 12, 13 | reported as unsupported with a reason |

Other things worth knowing:

- An operation may write to **several destination extents**, in the order the
  manifest lists them, and that is handled the way AOSP's `DirectExtentWriter`
  does it. Readers that assume one extent per operation silently corrupt images.
- A `payload.bin` **stored** uncompressed inside an OTA zip is read in place,
  so a multi-gigabyte package is never unpacked to disk first. ZIP64 packages
  are handled.
- Partitions are decoded **in parallel**, one worker per core by default.
- SHA-256 is checked per operation and again over the finished image.
- Incremental packages are still not applied; they are now reported clearly,
  per partition, instead of failing with an opaque message.

## Usage

- Download `Release.zip` from [Github Releases](https://github.com/xzr467706992/FastbootEnhance/releases)
- Unzip
- Click `FastbootEnhance.exe`

The published build is self-contained, so nothing has to be installed first.

A payload can also be opened directly: `FastbootEnhance.exe ota.zip`, or drop the
package onto the executable.

## Project layout

| Path | What it is |
| --- | --- |
| `./` | the WPF app (`net8.0-windows`) |
| `src/FastbootEnhance.Core` | payload parsing and extraction, no UI (`netstandard2.0` + `net8.0`) |
| `tests/FastbootEnhance.Core.Tests` | xunit suite that builds real payloads and checks them back |
| `tools/FastbootEnhance.PayloadTool` | command line front end for the core, used for testing and timing |

The protobuf types are generated at build time from
`src/FastbootEnhance.Core/Protos/update_metadata.proto`, so keeping up with
AOSP means replacing that one file.

## Building

```
dotnet build FastbootEnhance.csproj -c Release
dotnet test tests/FastbootEnhance.Core.Tests/FastbootEnhance.Core.Tests.csproj
```

To produce a single self-contained executable:

```
dotnet publish FastbootEnhance.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The `screenshots` workflow runs the published app on Windows against a sample OTA
from `tools/FastbootEnhance.SampleGen`, captures every tab into `docs/screenshots`,
and fails if the app does not start.

The app itself needs Windows to build because of WPF. The core library, its
tests and the command line tool build and run on Windows, Linux and macOS.

## Command line

```
fbe-payload info    <payload.bin|ota.zip>
fbe-payload extract <payload.bin|ota.zip> -o <dir> [-p name ...] [-j N] [--no-verify] [--force]
fbe-payload verify  <payload.bin|ota.zip> [-p name ...] [-j N]
```

## Note

- Incremental packages are not supported

( I don't have a plan to support it in the future because it is quite useless )

- Still you are able to extract correct image from incremental packages if the checksum passes

( The checksum will be automatically done if "ignore checksum" is not checked )

## Credits

- [Android Platform Tools](https://developer.android.com/studio/releases/platform-tools)
- [Protobuf](https://github.com/protocolbuffers/protobuf)
- [ZstdSharp](https://github.com/oleg-st/ZstdSharp)
- [SharpCompress](https://github.com/adamhathcock/sharpcompress)
