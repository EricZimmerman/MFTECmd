using System;
using CsvHelper;

namespace MFTECmd;

[CsvRecord]
public partial class I30Out
{
    public long Offset { get; set; }
    public bool FromSlack { get; set; }
    public ulong? SelfMftEntry { get; set; }
    public int? SelfMftSequence { get; set; }
    
    public string FileName { get; set; }
    public string Flags { get; set; }
    /// <summary>The name, as in the CSV (an enum would be a number in the JSON, Issues #263)</summary>
    public string NameType { get; set; }
    public ulong ParentMftEntry { get; set; }
    public int ParentMftSequence { get; set; }
    
    [CsvConverter(typeof(NullableDateConverter))]
    public DateTimeOffset? CreatedOn { get; set; }
    [CsvConverter(typeof(NullableDateConverter))]
    public DateTimeOffset? ContentModifiedOn { get; set; }
    [CsvConverter(typeof(NullableDateConverter))]
    public DateTimeOffset? RecordModifiedOn { get; set; }
    [CsvConverter(typeof(NullableDateConverter))]
    public DateTimeOffset? LastAccessedOn { get; set; }
    
    public ulong PhysicalSize { get; set; }
    public ulong LogicalSize { get; set; }
    
    public string SourceFile { get; set; }

    /// <summary>With -m: the path of the directory the entry is in (issue #194)</summary>
    public string ParentPath { get; set; }
}