using System;

namespace MFTECmd;

/// <summary>
///     One $LogFile record. The decoded columns (FileName through Details) are filled when the redo or undo data
///     holds a file record, an attribute, an index entry or timestamps; the paths need an $MFT (-m)
/// </summary>
public class LogFileOut
{
    public ulong Lsn { get; set; }
    public uint TransactionId { get; set; }

    /// <summary>The first record of this record's transaction: transaction ids are reused, this tells transactions apart</summary>
    public ulong TransactionFirstLsn { get; set; }

    public string RedoOperation { get; set; }
    public string UndoOperation { get; set; }

    /// <summary>
    ///     The file the operation changes: the $MFT entry for operations on file records, otherwise the file of the
    ///     open attribute (index buffers, non-resident data, bitmaps) from the log's checkpoints
    /// </summary>
    public ulong? TargetEntryNumber { get; set; }

    public uint? TargetSequenceNumber { get; set; }

    /// <summary>For operations on open attributes: the attribute changed (such as IndexAllocation $I30, or Data $J)</summary>
    public string TargetAttributeType { get; set; }

    public string TargetAttributeName { get; set; }

    /// <summary>With -m: the full path of the target in that $MFT</summary>
    public string TargetPath { get; set; }

    /// <summary>The file the decoded data is about (the entry an index entry points to, or the logged FILE record)</summary>
    public ulong? EntryNumber { get; set; }

    public uint? SequenceNumber { get; set; }
    public string FileName { get; set; }
    public ulong? ParentEntryNumber { get; set; }
    public uint? ParentSequenceNumber { get; set; }
    public string ParentPath { get; set; }

    public DateTimeOffset? CreatedOn { get; set; }
    public DateTimeOffset? ContentModifiedOn { get; set; }
    public DateTimeOffset? RecordModifiedOn { get; set; }
    public DateTimeOffset? LastAccessedOn { get; set; }

    public ulong? FileSize { get; set; }
    public string FileAttributes { get; set; }

    /// <summary>The attribute created or deleted, or the attribute an update applies to when it is known</summary>
    public string AttributeType { get; set; }

    /// <summary>What the decoded data is, and previous values from the undo data</summary>
    public string Details { get; set; }

    public ulong PreviousLsn { get; set; }
    public ulong UndoNextLsn { get; set; }
    public string RecordType { get; set; }
    public ushort? TargetAttribute { get; set; }

    /// <summary>The target attribute comes from across missing log data (left from an older pass), so it may be wrong</summary>
    public bool TargetInferred { get; set; }

    public long? TargetVcn { get; set; }
    public string Lcns { get; set; }
    public ushort? RecordOffset { get; set; }
    public ushort? AttributeOffset { get; set; }
    public ushort? ClusterBlockOffset { get; set; }
    public ushort? RedoLength { get; set; }
    public ushort? UndoLength { get; set; }

    /// <summary>The record continues into a page that is missing or damaged, so its data is cut short</summary>
    public bool Truncated { get; set; }

    /// <summary>Where the record header is in the $LogFile</summary>
    public long FileOffset { get; set; }

    public string SourceFile { get; set; }
}
