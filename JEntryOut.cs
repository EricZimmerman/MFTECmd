using System;
using System.IO;

namespace MFTECmd;

public class JEntryOut
{
    public string Name { get; set; }
    public string Extension => $"{Path.GetExtension(Name)}{string.Empty}";
    public ulong EntryNumber { get; set; }
    public uint SequenceNumber { get; set; }
    public ulong ParentEntryNumber { get; set; }
    public uint ParentSequenceNumber { get; set; }
    public string ParentPath { get; set; }

    public ulong UpdateSequenceNumber { get; set; }

    public DateTimeOffset UpdateTimestamp { get; set; }

    public string UpdateReasons { get; set; }
    public string FileAttributes { get; set; }
    public long OffsetToData { get; set; }
    public string SourceFile { get; set; }
}