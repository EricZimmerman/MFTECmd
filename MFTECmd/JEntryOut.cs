using System;
using CsvHelper;

namespace MFTECmd;

[CsvRecord]
public partial class JEntryOut
{
    public string Name { get; set; }
    /// <summary>The extension of <see cref="Name" /> (settable: CsvHelper 34 writes only members it can set)</summary>
    public string Extension { get; set; }
    public ulong EntryNumber { get; set; }
    public uint SequenceNumber { get; set; }
    public ulong ParentEntryNumber { get; set; }
    public uint ParentSequenceNumber { get; set; }
    public string ParentPath { get; set; }

    public ulong UpdateSequenceNumber { get; set; }

    [CsvConverter(typeof(DateConverter))]
    public DateTimeOffset UpdateTimestamp { get; set; }

    public string UpdateReasons { get; set; }
    public string FileAttributes { get; set; }
    public long OffsetToData { get; set; }
    public string SourceFile { get; set; }
}