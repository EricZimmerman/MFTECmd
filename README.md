# MFTECmd

Parses the NTFS metadata files `$MFT`, `$J` (`$UsnJrnl:$J`), `$LogFile`, `$Boot`, `$Secure:$SDS` and `$I30`, detecting
the file type from its content. Results can be exported to CSV and JSON, and the `$MFT` also to bodyfile. Locked files
and Volume Shadow Copies (`--vss`) are read through raw disk access.

| | |
|---|---|
| **Version** | 2026.10.5 |
| **Download** | [.NET 10](https://download.ericzimmermanstools.com/net/MFTECmd.zip) (recommended) &middot; [.NET Framework 4.7.2](https://download.ericzimmermanstools.com/MFTECmd.zip) |
| **All tools** | [ericzimmerman.github.io](https://ericzimmerman.github.io) or [Get-ZimmermanTools](https://download.ericzimmermanstools.com/Get-ZimmermanTools.zip), which downloads them all and keeps them current |
| **Changes** | [ChangeLog](https://ericzimmerman.github.io/changelog/) |
| **Issues** | [EricZimmerman/MFTECmd](https://github.com/EricZimmerman/MFTECmd/issues) or [EricZimmerman/Issues](https://github.com/EricZimmerman/Issues/issues) |

## Requirements

- **.NET 10 build**: the [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0). Run `MFTECmd.exe` on Windows,
  or `dotnet MFTECmd.dll` on any system with the runtime.
- **.NET Framework build**: .NET Framework 4.7.2 or later (Windows only).

## Command line

```text
Usage:
  MFTECmd [options]

Options:
  -f <f>             File to process ($MFT | $J | $LogFile | $Boot | $SDS | $I30). Required
  -m <m>             $MFT file to use when -f points to a $J or $LogFile file, to resolve parent paths. For $J, the paths are as they were when each record was written
  --csv <csv>        Directory to save CSV formatted results to. Be sure to include the full path in double quotes. This or --json required unless --de or --body is specified
  --csvf <csvf>      File name to save CSV formatted results to. When present, overrides default name
  --json <json>      Directory to save JSON formatted results to. Be sure to include the full path in double quotes. This or --csv required unless --de or --body is specified
  --jsonf <jsonf>    File name to save JSON formatted results to. When present, overrides default name
  --body <body>      Directory to save bodyfile formatted results to. --bdl is also required when using this option
  --bodyf <bodyf>    File name to save body formatted results to. When present, overrides default name
  --bdl <bdl>        Drive letter (C, D, etc.) to use with bodyfile. Only the drive letter itself should be provided
  --blf              When true, use LF vs CRLF for newlines
  --dd <dd>          Directory to save exported $MFT FILE record. --do is also required when using this option
  --do <do>          Offset of the $MFT FILE record to dump as decimal or hex. Ex: 5120 or 0x1400 Use --de or --debug to see offsets
  --de <de>          Dump full details for $MFT entry/sequence #. Format is 'Entry' or 'Entry-Seq' as decimal or hex. Example: 5, 624-5 or 0x270-0x5.
  --dr               When true, dump $MFT resident files to dir specified by --csv or --json, in 'Resident' subdirectory. Files will be named '<EntryNumber>-<SequenceNumber>-<AttributeNumber>_<FileName>.bin'
  --fls              When true, displays contents of directory from $MFT specified by --de. Ignored when --de points to a file
  --ds <ds>          Dump full details for Security Id from $SDS as decimal or hex. Example: 624 or 0x270
  --dt <dt>          The custom date/time format to use when displaying time stamps. See https://goo.gl/CNVq0k for options [default: yyyy-MM-dd HH:mm:ss.fffffff]
  --sn               Include DOS file name types in $MFT output
  --fl               Generate condensed file listing of parsed $MFT contents. Requires --csv
  --at               When true, include all timestamps from 0x30 attribute vs only when they differ from 0x10 in the $MFT
  --rs               When true, recover slack space from FILE records when processing $MFT files. This option has no effect for $I30 files
  --vss              Process all Volume Shadow Copies that exist on drive specified by -f
  --dedupe           Deduplicate -f & VSCs based on SHA-1. First file found wins
  --ir               Include resident data in JSON/CSV output
  --re <re>          Comma-separated list of extensions to include for resident data (e.g., '.txt,.ps1,.bat'). If omitted, includes all
  --rm <rm>          Maximum size in bytes for resident data to include (max: 1024000) [default: 1024]
  --cutoff <cutoff>  Only include $MFT records whose $STANDARD_INFO created or modified time is on or after this date/time (UTC unless an offset is given). Example: 2025-10-19 or 2025-10-19T13:45:30Z
  --lowmem           Use less memory at the cost of speed (workstation instead of server garbage collection): about 30% less peak memory, and about 1.4x slower
  --debug            Show debug information during processing
  --trace            Show trace information during processing
  --co               Save all console messages to <timestamp>_MFTECmd_<version>_ConsoleLog.txt in the output directory (the first of --csv, --json, --body given)
  -?, -h, --help     Show help and usage information
  --version          Show version information
```

Short options (a single letter) start with one dash, long options with two.

## Examples

```text
MFTECmd.exe -f "C:\Temp\SomeMFT" --csv "c:\temp\out" --csvf MyOutputFile.csv
MFTECmd.exe -f "C:\Temp\SomeMFT" --csv "c:\temp\out"
MFTECmd.exe -f "C:\Temp\SomeMFT" --json "c:\temp\jsonout"
MFTECmd.exe -f "C:\Temp\SomeMFT" --body "c:\temp\bout" --bdl c
MFTECmd.exe -f "C:\Temp\SomeMFT" --de 5-5
MFTECmd.exe -f "C:\Temp\SomeMFT" --csv "c:\temp\out" --dr --fl
MFTECmd.exe -f "c:\temp\SomeJ" --csv "c:\temp\out"
MFTECmd.exe -f "c:\temp\SomeJ" -m "C:\Temp\SomeMFT" --csv "c:\temp\out"
MFTECmd.exe -f "c:\temp\SomeLogFile" --csv "c:\temp\out"
MFTECmd.exe -f "c:\temp\SomeLogFile" -m "C:\Temp\SomeMFT" --json "c:\temp\jsonout"
MFTECmd.exe -f "c:\temp\SomeBoot"
MFTECmd.exe -f "c:\temp\SomeSecure_SDS" --csv "c:\temp\out"
MFTECmd.exe -f "c:\temp\SomeI30" --csv "c:\temp\out"
```

## Output

- **File names**: `<timestamp>_MFTECmd_<type>_Output.<ext>` (for example `20261023120000_MFTECmd_$MFT_Output.csv`)
  unless `--csvf`, `--jsonf` or `--bodyf` is given. Copies from Volume Shadow Copies get the VSC number and creation
  time in the name.
- **`$MFT`**: CSV (`--csv`), JSON (`--json`) and bodyfile (`--body` with `--bdl`). The CSV ends with `ParseErrors`: the
  problems in that FILE record (damaged attributes, fixups) that were skipped. With `--ir` the resident data columns
  (`ResidentDataBase64`, `ResidentDataHex`, `ResidentDataASCII`) come before it; without `--ir` they are not written. `--fl` also writes a condensed file
  listing (`..._FileListing.csv`), `--dr` saves resident files to a `Resident` folder, and `--de` shows every detail of
  one entry on the console.
- **`$J`** and **`$LogFile`**: CSV and JSON, one row per record (see the sections below).
- **`$Boot`**: shown on the console, and saved to CSV with `--csv`.
- **`$SDS`**: CSV, one row per security id (`FromMirror` marks mirror copies that differ); `--ds` shows one on the
  console.
- **`$I30`**: CSV.
- **JSON**: one object per line, time stamps in ISO 8601 UTC (`2024-01-02T03:04:05.6780000Z`).
- **Console log** (`--co`): everything shown on the console, with times and levels, in
  `<timestamp>_MFTECmd_<version>_ConsoleLog.txt` (UTC) in the first of the `--csv`, `--json` and `--body` directories
  given.

Damaged records, pages and entries are skipped and reported (details with `--debug`), and the rest of the file is still
processed.

## $J paths with an $MFT (-m)

With `-f $J -m $MFT`, the `ParentPath` of each $J record is the path its parent directory had when the record was
written. Starting from the directories in the $MFT (the state at the end of the journal), the journal is "rewound"
from its newest record to its oldest: a `RenameOldName` record gives the name and parent a directory had before the
rename, and a record of a directory that the $MFT no longer has (such as its `FileDelete`) gives its name and parent at
that time. Records are taken in USN order, the order they were written, so clock changes do not matter. Paths
through directories that were deleted, reused, renamed or moved resolve, where the $MFT alone gives `PathUnknown` or
today's location. Use the $MFT and $J of the same volume, collected at the same time. Without `-m`, `ParentPath` is
empty.

This is the technique, and a reimplementation of the proof of concept (MIT), by Yogesh Khatri of CyberCX:
[NTFS Usnjrnl Rewind](https://cybercx.com.au/blog/ntfs-usnjrnl-rewind/) and
[usnjrnl_rewind](https://github.com/CyberCX-DFIR/usnjrnl_rewind). The $MFT is exported too, as before, with its default
file names (`--csvf`/`--jsonf` name the $J output).

## $LogFile

`-f $LogFile` exports one row per log record (CSV and/or JSON), ordered by LSN:

- `Lsn`, `TransactionId`, `TransactionFirstLsn`, `RedoOperation`, `UndoOperation`: what the record does. Redo data is
  the change, undo data what was there before. Transaction ids are reused; `TransactionFirstLsn` (the transaction's
  first record) tells transactions apart.
- `TargetEntryNumber` through `TargetPath`: what the operation changes. For operations on file records, the $MFT entry
  (the volume's cluster size is worked out from the FILE records in the log). For the others (index buffers,
  non-resident data such as `$UsnJrnl:$J`, bitmaps), the file and attribute from the log's checkpoints (the open
  attribute table) and open records; `TargetInferred` is true when that comes from across missing log data. With
  `-m`, `TargetPath` is the target's path in that $MFT.
- `EntryNumber` through `AttributeType`: decoded from the data when it is a FILE record that was written, an attribute
  that was created or deleted (such as `$FILE_NAME` or `$STANDARD_INFORMATION`), a file name index entry that was added
  or deleted (file name, parent, timestamps and size), a directory entry update (timestamps and size), or an update
  of `$STANDARD_INFORMATION` timestamps (such as a time stomp). With `-m`, `ParentPath` is the parent's path in that
  $MFT.
- `Details`: what was decoded, with the previous values from the undo data.
- `PreviousLsn`, `UndoNextLsn`, `RecordType`, `TargetAttribute`, `TargetInferred`, `TargetVcn`, `Lcns`, `RecordOffset`,
  `AttributeOffset`, `ClusterBlockOffset`, `RedoLength`, `UndoLength`, `Truncated`, `FileOffset`: the rest of the
  record.

## Memory (--lowmem)

`--lowmem` runs with workstation instead of server garbage collection: about 30% less peak memory, about 1.4x slower
(a 2 GB $MFT: 4.7 GB -> 3.3 GB, 11.7 s -> 15.8 s). The .NET Framework build always uses workstation garbage collection.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success. Damaged, empty or unknown files are reported but are not a failure |
| `1` | The tool failed: invalid arguments, a missing file, a file it can't read (access denied, or in use and the raw read also failed, IO errors), or output that can't be written |

## What's new in 2026.10

- Large $MFTs load about 1.8x faster (2.1M records: 10.4 s -> 5.7 s) and take about 20% less memory; the new
  `--lowmem` option uses about 30% less peak memory for about 1.4x the run time.
- `$LogFile` support: every log record to CSV and JSON, decoding FILE records, attributes, file name index entries and
  `$STANDARD_INFORMATION` timestamp changes (time stomping) with their previous values, plus each operation's target
  file and attribute and transaction grouping.
- `$J` with `-m $MFT`: parent paths are as they were when each record was written (#56); v3 records are read and v4
  records skipped correctly.
- New `--cutoff` option, and a `ParseErrors` column at the end of the $MFT CSV.
- The $MFT CSV only has the resident data columns with `--ir` (#58).
- $MFTs over 2 GB are supported, one damaged record no longer ends the whole $MFT, and resident attribute lists are
  read.
- `$SDS`: one row per security id (every entry was output twice) and correct SIDs.
- JSON time stamps end in `Z` instead of `+00:00`; JSON output replaces an existing file instead of leaving old data at
  its end.
- Exit code 1 when the tool fails (it was always 0). Error reports include source file and line numbers.
- Warnings about invalid timestamps say where they are (attribute, FILE record and offset, $I30 offset or $LogFile
  LSN) and show the raw value (2026.10.4).
- New `--co`: saves all console messages to `<timestamp>_MFTECmd_<version>_ConsoleLog.txt` in the output directory
  (Issues #259). --debug and --trace show the time of each message again (they showed the --dt date) (2026.10.5)

The full list is in the [ChangeLog](https://ericzimmerman.github.io/changelog/).

## Building

```text
dotnet build MFTECmd.slnx -c Release
```

Builds `net10.0` and `net472`. Parsing is in the [MFT](https://github.com/EricZimmerman/MFT) libraries (NuGet packages
[MFT](https://www.nuget.org/packages/MFT), `Usn`, `MFT.LogFile`, `Boot`, `Secure` and `I30`).

## Further reading

- [Introducing MFTECmd!](https://binaryforay.blogspot.com/2018/06/introducing-mftecmd.html)
- [MFTECmd v0.2.6.0 released](https://binaryforay.blogspot.com/2018/06/mftecmd-v0260-released.html)
- [MFTECmd 0.3.6.0 released](https://binaryforay.blogspot.com/2018/12/mftecmd-0360-released.html)
- [Locked file support added to AmcacheParser, AppCompatCacheParser, MFTECmd, ShellBags Explorer (and SBECmd), and Registry Explorer (and RECmd)](https://binaryforay.blogspot.com/2019/01/locked-file-support-added-to.html)
- [NTFS Usnjrnl Rewind](https://cybercx.com.au/blog/ntfs-usnjrnl-rewind/) (Yogesh Khatri, CyberCX)

## Special thanks

Open source development funding and support provided by the [SANS Institute](https://www.sans.org/) and
[SANS DFIR](https://www.sans.org/digital-forensics-incident-response/).

## License

[MIT](LICENSE), Copyright (c) Eric R. Zimmerman 2027.
