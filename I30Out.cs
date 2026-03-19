using System;
using MFT.Attributes;

namespace MFTECmd;

public class I30Out
{
    public long Offset { get; set; }
    public bool FromSlack { get; set; }
    public uint? SelfMftEntry { get; set; }
    public int? SelfMftSequence { get; set; }
    
    public string FileName { get; set; }
    public string Flags { get; set; }
    public NameTypes NameType { get; set; }
    public uint ParentMftEntry { get; set; }
    public int ParentMftSequence { get; set; }
    
    public DateTimeOffset? CreatedOn { get; set; }
    public DateTimeOffset? ContentModifiedOn { get; set; }
    public DateTimeOffset? RecordModifiedOn { get; set; }
    public DateTimeOffset? LastAccessedOn { get; set; }
    
    public ulong PhysicalSize { get; set; }
    public ulong LogicalSize { get; set; }
    
    public string SourceFile { get; set; }
}