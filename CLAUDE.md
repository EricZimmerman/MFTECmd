# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

MFTECmd is a .NET console tool, part of Eric Zimmerman's DFIR tool suite, that parses NTFS metadata files ($MFT, $J, $LogFile, $Boot, $Secure:$SDS, $I30) and exports them to CSV, JSON and bodyfile (bodyfile is $MFT only). The parsers are NuGet packages from the MFT repo (EricZimmerman/MFT): `MFT`, `Usn`, `MFT.LogFile` (namespace `LogFile`), `Boot`, `Secure`, `I30`. A fix to how a structure is parsed belongs there, followed by a package bump in `MFTECmd/MFTECmd.csproj`. Input may be corrupt or hostile.

## Commands

The SDK is pinned in `global.json` (.NET 10). The CLI targets `net472;net10.0`, tests `net10.0`, all with `LangVersion 10`. Tests use NUnit 5 + FluentAssertions 7 (pinned to `[7.2.2,8.0.0)`, since 8.x requires a commercial license).

```bash
dotnet build -c Release                                     # MFTECmd.slnx: both CLI targets and the tests
dotnet test -c Release                                      # what CI runs (.github/workflows/build.yml)
dotnet test --filter "FullyQualifiedName~RewindTests"       # one fixture
dotnet run --project MFTECmd -f net10.0 -- -f '$J' -m '$MFT' --csv out
```

## Architecture

Almost everything is in `MFTECmd/Program.cs`.

- **`Main`** builds a `System.CommandLine` `RootCommand` and returns `DoWork`'s exit code, only 0 or 1: 1 is a failure of the tool (invalid arguments, a missing file, a file that can't be read: access denied, in use and the raw read failed, IO error; or a failed export); a damaged, empty or unknown file is reported and exits 0 (`LoadFailed` decides by the first error). Adding an option takes three steps: create the `Option<T>`, add it to the `RootCommand` initializer, and pass `result.GetValue(...)` to `DoWork`.
- **`GetFileType`** detects the type from the content: `FILE` ($MFT), `RSTR`/`CHKD` ($LogFile), `INDX` ($I30), `NTFS` at 3 ($Boot), USN record version 2 or a sparse (zero) start ($J), otherwise $SDS heuristics. It returns null when the file can't be read.
- **`Process*`** (one per type) return false on failure. Each loads `-f` and, with `--vss`, the same file from every VSC (raw copies, also used when `-f` is locked). Outputs are named `<timestamp>_MFTECmd_<type>_Output.<ext>` (`GetOutName`; VSC copies get the VSC number and time) unless `--csvf`/`--jsonf` is given. $J and $LogFile write through `WriteCsv`/`WriteJson`. JSON is one object per line, using ServiceStack.Text.
- **`-m $MFT`** with `-f $J` or `-f $LogFile`: `ProcessMft` runs first, which also exports the $MFT (with default names: `--csvf`/`--jsonf`/`--bodyf` are for `-f`), and sets `_mft`/`_mftFiles`. `GetMftFor` pairs a VSC's $J/$LogFile with the $MFT from the same VSC.
- **`--lowmem`**: the GC mode is fixed at startup, so `RunWithWorkstationGc` starts MFTECmd again with the same arguments, `DOTNET_gcServer=0` and the `MFTECMD_LOWMEM` marker, and returns its exit code. Only the net10.0 build has server GC (`ServerGarbageCollection` does not reach the net472 app.config). `--debug` logs the GC mode.
- **$J (`UsnRewind`)**: with an $MFT, `ParentPath` is "rewound" (issue #56; Yogesh Khatri's technique, CyberCX). It seeds the state from the $MFT's directories (a free record's last occupant has sequence - 1), then walks the records newest to oldest in **USN order**, not by timestamp, because clocks go backwards in real journals. `RenameOldName` sets an entry's earlier name and parent, and the newest record of an entry that isn't known yet adds it. Paths are cached per parent until a change touches a key they used. An unresolved ancestor gives `.\PathUnknown\Directory with ID 0x<entry>-<seq>`, like `Mft.GetFullParentPath`. Without `-m`, `ParentPath` is empty.
- **$LogFile (`LogFileRows`, `LogFileOut`)**: one row per log record, ordered by LSN. Operations on file records target an $MFT entry (`TargetMftEntry`); the others target an open attribute (`NtfsLogRecord.TargetOpenAttribute`, from the checkpoints' open attribute table and open records, decoded in MFT.LogFile), which fills `TargetEntryNumber`/`TargetSequenceNumber`/`TargetAttributeType`/`TargetAttributeName` (with `-m`, a name the log lacks comes from the $MFT record) and `TargetInferred`. `TransactionFirstLsn` groups a transaction's records. `LoadLogFile` works out the cluster size from the logged FILE records (their header entry number vs target VCN), defaulting to 4096. Rows are decoded from FILE records, created or deleted attributes, $I30 index entries, `UpdateFileName*` and `UpdateResidentValue` of `$STANDARD_INFORMATION`. That last one is the first attribute (record offset = the record's first attribute offset, from `-m` or the logged FILE records, else 0x38), and its value starts at attribute offset 0x18. Index entries of other indexes ($O etc.) don't start with a file reference, so they only get `Details`. With `-m`, `TargetPath` is the current occupant of the target entry number.
- **CSV columns** (`MFTRecordOut`, `JEntryOut`, `LogFileOut`, `I30Out`, `SdsOut`, `BootOut`): keep existing columns in order and add new ones at the end (the $MFT CSV ends with `ParseErrors`, from `FileRecord.HasParseErrors`/`ParseErrors`; the three `ResidentData*` columns before it are only written with `--ir`, issue #58, and TLEFilePlugins has a plugin for each header). Timeline Explorer and users' scripts depend on the positions. The $MFT CSV order is set with `.Index()` in `ProcessMft`.
- `#if NET9_0_OR_GREATER` separates net472 (AlphaFS `File`/`Directory`/`Path`) from net10.0. Build both targets.
- Costura.Fody embeds the dependencies. Server GC is on (about 2x faster $MFT loads, about 30% more memory).

## Tests

`MFTECmd.Test` references the CLI project. `TestFiles/` has the license-clean samples: `Generated/` (ntfsprogs, from the MFT repo) and `DFIRArtifactMuseum/Win2012R2/StolenSzechuan` (MIT; `.gz` files are decompressed by the tests, and `$MFT_directories.gz` is that system's $MFT with only system files and directories kept).
- `RewindTests`: `UsnRewind` with the generated $MFT and journals from `UsnBuilder` (USN_RECORD_V2) covering renames, moves, deletes, reused entries and loops.
- `CliTests`: runs the built `MFTECmd.dll` with `dotnet` for exit codes, CSV/JSON output of every type, `ParseErrors` (a damaged copy of the generated $MFT), `--lowmem` (same output), and the StolenSzechuan $J (+ $MFT) and $LogFile (decoded values, open attribute targets, transactions). The $J paths are pinned by a SHA-256; they match usnjrnl_rewind for all 82,151 records (14,031 were PathUnknown before).

Larger validations (old vs new output, usnjrnl_rewind on the other Museum $MFT/$J pairs, $LogFile $SI timestamps vs the $MFT) were run by hand. The DFIR Artifact Museum (https://github.com/AndrewRathbun/DFIRArtifactMuseum, `Windows/NTFSArtifacts`) has $MFT, $J, $LogFile and $Boot from the same systems. A local $MFT corpus may be in `../Corpus/MFTs`; never copy its files into the repo.

## Versioning and release

- `<Version>` in `MFTECmd/MFTECmd.csproj` (calendar style, e.g. `2026.10.1`), bumped by hand. The CLI is not packed. A release is a bare tag plus `gh release create <ver> --verify-tag --generate-notes`, and the binaries are built, signed and staged manually.
- Dependabot (`.github/dependabot.yml`) opens monthly PRs for GitHub Actions and NuGet, and ignores FluentAssertions 8.x.
- Release builds are staged, signed and zipped by `Build-ZimmermanTools.ps1` in the get-zimmermantools repository (cloned next to this one). Its program table says what goes in this tool's download zip (the files taken from the build, folders copied from the repositories, files kept from the current release zip); a new output file, a content folder or a change in how the tool is built (single file, another framework) needs that entry updated.
- pdbs are embedded in the assemblies (`DebugType embedded`, `EmbedUntrackedSources`) and Release builds set `ContinuousIntegrationBuild` (source paths `/_/...`), both in `Directory.Build.props`: stack traces and Exceptionless reports have file and line numbers, and no .pdb file ships. Don't add `DebugType` (or `DebugSymbols`, `IncludeSymbols`, snupkg) to project files.
