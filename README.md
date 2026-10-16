# MFTECmd

## Command Line Interface

    MFTECmd version 2026.10.1

    Author: Eric Zimmerman (saericzimmerman@gmail.com)
    https://github.com/EricZimmerman/MFTECmd

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
    --debug            Show debug information during processing
    --trace            Show trace information during processing

    Examples: MFTECmd.exe -f "C:\Temp\SomeMFT" --csv "c:\temp\out" --csvf MyOutputFile.csv
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

              Short options (single letter) are prefixed with a single dash. Long commands are prefixed with two dashes

The exit code is 0 on success, and 1 for invalid arguments, a file that could not be processed or an export that failed.

## $J paths with an $MFT (-m)

With `-f $J -m $MFT`, the `ParentPath` of each $J record is the path its parent directory had when the record was
written. Starting from the directories in the $MFT (the state at the end of the journal), the journal is "rewound"
from its newest record to its oldest: a `RenameOldName` record gives the name and parent a directory had before the
rename, and a record of a directory that the $MFT no longer has (such as its `FileDelete`) gives its name and parent at
that time. Records are taken in USN order, the order they were written, so clock changes do not matter. Paths
through directories that were deleted, reused, renamed or moved resolve, where the $MFT alone gives `PathUnknown` or
today's location. Use the $MFT and $J of the same volume, collected at the same time.

This is the technique, and a reimplementation of the proof of concept (MIT), by Yogesh Khatri of CyberCX:
[NTFS Usnjrnl Rewind](https://cybercx.com.au/blog/ntfs-usnjrnl-rewind/) and
[usnjrnl_rewind](https://github.com/CyberCX-DFIR/usnjrnl_rewind). The $MFT is exported too, as before.

## $LogFile

`-f $LogFile` exports one row per log record (CSV and/or JSON), ordered by LSN:

- `Lsn`, `TransactionId`, `RedoOperation`, `UndoOperation`: what the record does. Redo data is the change, undo data
  what was there before.
- `TargetEntryNumber`: the $MFT entry changed, for operations on file records (the volume's cluster size is worked
  out from the FILE records in the log). With `-m`, `TargetPath` is the path of the record with that entry number in
  that $MFT.
- `EntryNumber` through `AttributeType`: decoded from the data when it is a FILE record that was written, an attribute
  that was created or deleted (such as `$FILE_NAME` or `$STANDARD_INFORMATION`), a file name index entry that was added
  or deleted (file name, parent, timestamps and size), a directory entry update (timestamps and size), or an update
  of `$STANDARD_INFORMATION` timestamps (such as a time stomp). With `-m`, `ParentPath` is the parent's path in that
  $MFT.
- `Details`: what was decoded, with the previous values from the undo data.
- `PreviousLsn`, `UndoNextLsn`, `RecordType`, `TargetAttribute`, `TargetVcn`, `Lcns`, `RecordOffset`,
  `AttributeOffset`, `ClusterBlockOffset`, `RedoLength`, `UndoLength`, `Truncated`, `FileOffset`: the rest of the
  record.

## Documentation

MFT parser for NTFS file systems.

[Introducing MFTECmd!](https://binaryforay.blogspot.com/2018/06/introducing-mftecmd.html)

[MFTECmd v0.2.6.0 released](https://binaryforay.blogspot.com/2018/06/mftecmd-v0260-released.html)

[MFTECmd 0.3.6.0 released](https://binaryforay.blogspot.com/2018/12/mftecmd-0360-released.html)

[Locked file support added to AmcacheParser, AppCompatCacheParser, MFTECmd, ShellBags Explorer (and SBECmd), and Registry Explorer (and RECmd)](https://binaryforay.blogspot.com/2019/01/locked-file-support-added-to.html)

# Download Eric Zimmerman's Tools

All of Eric Zimmerman's tools can be downloaded [here](https://ericzimmerman.github.io/#!index.md). 

# Special Thanks

Open Source Development funding and support provided by the following contributors: 
- [SANS Institute](http://sans.org/) and [SANS DFIR](http://dfir.sans.org/).
- [Tines](https://www.tines.com/?utm_source=oss&utm_medium=sponsorship&utm_campaign=ericzimmerman)
