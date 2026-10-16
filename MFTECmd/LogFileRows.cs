using System;
using System.Collections.Generic;
using System.Linq;
using LogFile;
using MFT;
using MFT.Attributes;
using MFT.Other;
using Attribute = MFT.Attributes.Attribute;

namespace MFTECmd;

/// <summary>Turns $LogFile records into <see cref="LogFileOut" /> rows, with paths from an $MFT when there is one</summary>
public class LogFileRows
{
    /// <summary>The resident attribute header before $STANDARD_INFORMATION's value</summary>
    private const int ResidentHeaderSize = 0x18;

    /// <summary>Where the first attribute (always $STANDARD_INFORMATION) starts in NTFS 3.1 FILE records</summary>
    private const int DefaultFirstAttributeOffset = 0x38;

    private static readonly string[] SiTimestampNames = { "Created", "Modified", "Record changed", "Accessed" };

    private readonly Dictionary<long, FileRecord> _byEntry = new();
    private readonly int _firstAttributeOffset;
    private readonly Mft _mft;
    private readonly string _sourceFile;
    private readonly Dictionary<long, string> _targetPaths = new();

    /// <param name="mft">The $MFT of the same volume, or null</param>
    public LogFileRows(LogFile.LogFile log, Mft mft, string sourceFile)
    {
        _mft = mft;
        _sourceFile = sourceFile;

        if (mft != null)
        {
            //in use records win over free records with the same entry number
            foreach (var record in mft.FreeFileRecords.Values.Concat(mft.FileRecords.Values))
            {
                _byEntry[record.EntryNumber] = record;
            }
        }

        //$STANDARD_INFORMATION is the first attribute; where that is depends on the size of the fixup array
        var offsets = log.Records.Select(t => t.Ntfs?.FileRecord).Where(t => t != null && t.FirstAttributeOffset > 0)
            .GroupBy(t => t.FirstAttributeOffset).OrderByDescending(t => t.Count()).FirstOrDefault();
        _firstAttributeOffset = offsets?.Key ?? DefaultFirstAttributeOffset;
    }

    public LogFileOut GetRow(LogRecord record)
    {
        var row = new LogFileOut
        {
            Lsn = record.Lsn,
            PreviousLsn = record.ClientPreviousLsn,
            UndoNextLsn = record.ClientUndoNextLsn,
            TransactionId = record.TransactionId,
            TransactionFirstLsn = record.TransactionFirstLsn,
            RecordType = record.RecordType.ToString(),
            Truncated = record.Truncated,
            FileOffset = record.FileOffset,
            SourceFile = _sourceFile
        };

        var ntfs = record.Ntfs;
        if (ntfs == null)
        {
            if (record.RecordType == LogRecordType.ClientRestart)
            {
                row.Details = record.Restart == null || record.Restart.OpenAttributeTableLsn == 0
                    ? "Checkpoint (NTFS restart area), without tables"
                    : $"Checkpoint (NTFS restart area {record.Restart.MajorVersion}.{record.Restart.MinorVersion}): open attribute table at LSN {record.Restart.OpenAttributeTableLsn}, attribute names at LSN {record.Restart.AttributeNamesLsn}";
            }

            return row;
        }

        row.RedoOperation = ntfs.RedoOperation.ToString();
        row.UndoOperation = ntfs.UndoOperation.ToString();
        row.TargetEntryNumber = (ulong?)ntfs.TargetMftEntry;
        row.TargetAttribute = ntfs.TargetAttribute;
        row.TargetVcn = ntfs.TargetVcn;
        row.Lcns = string.Join("|", ntfs.Lcns);
        row.RecordOffset = ntfs.RecordOffset;
        row.AttributeOffset = ntfs.AttributeOffset;
        row.ClusterBlockOffset = ntfs.ClusterBlockOffset;
        row.RedoLength = ntfs.RedoLength;
        row.UndoLength = ntfs.UndoLength;

        if (ntfs.TargetMftEntry.HasValue)
        {
            row.TargetPath = TargetPath(ntfs.TargetMftEntry.Value);
        }
        else if (ntfs.TargetOpenAttribute != null)
        {
            SetOpenAttribute(row, ntfs.TargetOpenAttribute);
            row.TargetInferred = ntfs.TargetOpenAttributeInferred;
        }

        try
        {
            Decode(ntfs, row);
        }
        catch (Exception e)
        {
            row.Details = $"Data could not be decoded: {e.Message}";
        }

        return row;
    }

    private void Decode(NtfsLogRecord ntfs, LogFileOut row)
    {
        if (ntfs.FileRecord != null)
        {
            var fr = ntfs.FileRecord;
            row.EntryNumber = fr.EntryNumber;
            row.SequenceNumber = fr.SequenceNumber;
            row.Details = $"FILE record written ({fr.EntryFlags.ToString().Replace(", ", "|")})";

            var fn = fr.Attributes.OfType<FileName>().FirstOrDefault(t => t.FileInfo.NameType != NameTypes.Dos) ??
                     fr.Attributes.OfType<FileName>().FirstOrDefault();
            if (fn != null)
            {
                SetFileInfo(row, fn.FileInfo);
            }

            var si = fr.Attributes.OfType<StandardInfo>().FirstOrDefault();
            if (si != null)
            {
                SetStandardInfo(row, si);
            }

            if (fr.IsDirectory() == false)
            {
                row.FileSize = fr.GetFileSize();
            }

            return;
        }

        if (ntfs.OpenAttributeTable != null)
        {
            row.Details = $"Checkpoint: open attribute table ({ntfs.OpenAttributeTable.Count:N0} attributes)";
            return;
        }

        if (ntfs.AttributeNames != null)
        {
            row.Details = $"Checkpoint: attribute names ({ntfs.AttributeNames.Count:N0})";
            return;
        }

        if (ntfs.OpenedAttribute != null)
        {
            row.Details = "Attribute opened";
            return;
        }

        switch (ntfs.RedoOperation)
        {
            case NtfsOperation.DeallocateFileRecordSegment:
                row.Details = "FILE record freed";
                return;

            case NtfsOperation.CreateAttribute when ntfs.RedoAttribute != null:
                SetAttribute(row, ntfs.RedoAttribute, ntfs.TargetMftEntry, "Attribute created");
                return;

            case NtfsOperation.DeleteAttribute when ntfs.UndoAttribute != null:
                SetAttribute(row, ntfs.UndoAttribute, ntfs.TargetMftEntry, "Attribute deleted");
                return;

            case NtfsOperation.AddIndexEntryRoot or NtfsOperation.AddIndexEntryAllocation when ntfs.RedoIndexEntry != null:
                SetIndexEntry(row, ntfs.RedoIndexEntry, "Index entry added");
                return;

            case NtfsOperation.DeleteIndexEntryRoot or NtfsOperation.DeleteIndexEntryAllocation when ntfs.UndoIndexEntry != null:
                SetIndexEntry(row, ntfs.UndoIndexEntry, "Index entry deleted");
                return;

            case NtfsOperation.UpdateFileNameRoot or NtfsOperation.UpdateFileNameAllocation when ntfs.RedoFileNameUpdate != null:
                SetFileNameUpdate(row, ntfs.RedoFileNameUpdate, ntfs.UndoFileNameUpdate);
                return;

            case NtfsOperation.UpdateResidentValue:
                SetResidentValue(row, ntfs);
                return;
        }
    }

    /// <summary>The file and attribute an operation on an open attribute changes</summary>
    private void SetOpenAttribute(LogFileOut row, OpenAttribute target)
    {
        var reference = target.FileReference;
        row.TargetEntryNumber = reference.MftEntryNumber;
        row.TargetSequenceNumber = reference.MftSequenceNumber;
        row.TargetAttributeType = target.AttributeType.ToString();
        row.TargetAttributeName = target.AttributeName;

        if (_mft == null)
        {
            return;
        }

        var key = reference.GetKey();
        if (_mft.FileRecords.TryGetValue(key, out var record) == false)
        {
            _mft.FreeFileRecords.TryGetValue(key, out record);
        }

        if (record == null)
        {
            //that entry has been reused since
            return;
        }

        if (row.TargetAttributeName == null)
        {
            //not in the log (Windows 7 open records): the record's only attribute of that type has it
            var named = record.Attributes.Where(t => t.AttributeType == target.AttributeType).Select(t => t.Name ?? string.Empty).Distinct().ToList();
            if (named.Count == 1)
            {
                row.TargetAttributeName = named[0];
            }
        }

        row.TargetPath = reference.MftEntryNumber == 5 ? "." : PathOf(record);
    }

    private string PathOf(FileRecord record)
    {
        var fn = record.Attributes.OfType<FileName>().FirstOrDefault(t => t.FileInfo.NameType != NameTypes.Dos) ??
                 record.Attributes.OfType<FileName>().FirstOrDefault();
        return fn == null ? null : $"{_mft.GetFullParentPath(fn.FileInfo.ParentMftRecord.GetKey())}\\{fn.FileInfo.FileName}";
    }

    private void SetAttribute(LogFileOut row, Attribute attribute, long? entry, string what)
    {
        row.AttributeType = attribute.AttributeType.ToString();
        row.Details = string.IsNullOrEmpty(attribute.Name) ? what : $"{what} (named '{attribute.Name}')";

        if (entry.HasValue)
        {
            row.EntryNumber = (ulong)entry.Value;
        }

        switch (attribute)
        {
            case FileName fn:
                SetFileInfo(row, fn.FileInfo);
                break;
            case StandardInfo si:
                SetStandardInfo(row, si);
                break;
            case Data { IsResident: true } data when data.ResidentData != null:
                row.FileSize = (ulong)data.ResidentData.Data.LongLength;
                break;
            case Data { IsResident: false } data when data.NonResidentData != null:
                row.FileSize = data.NonResidentData.ActualSize;
                break;
        }
    }

    private void SetIndexEntry(LogFileOut row, LoggedIndexEntry entry, string what)
    {
        if (entry.FileInfo == null)
        {
            //another index ($O, $Q, $R, $SDH, $SII): its entries do not start with a file reference
            row.Details = $"{what} (not a file name index)";
            return;
        }

        row.Details = what;
        row.EntryNumber = entry.MftReference.MftEntryNumber;
        row.SequenceNumber = entry.MftReference.MftSequenceNumber;
        row.AttributeType = nameof(MFT.Attributes.AttributeType.FileName);
        SetFileInfo(row, entry.FileInfo);
    }

    private static void SetFileNameUpdate(LogFileOut row, LoggedFileNameUpdate redo, LoggedFileNameUpdate undo)
    {
        row.AttributeType = nameof(MFT.Attributes.AttributeType.FileName);
        row.CreatedOn = redo.CreatedOn;
        row.ContentModifiedOn = redo.ContentModifiedOn;
        row.RecordModifiedOn = redo.RecordModifiedOn;
        row.LastAccessedOn = redo.LastAccessedOn;
        row.FileSize = redo.RealSize;
        row.FileAttributes = Flags(redo.FileAttributes);
        row.Details = "Directory entry updated";

        if (undo == null)
        {
            return;
        }

        var previous = new List<string>();
        AddIfChanged(previous, "Created", undo.CreatedOn, redo.CreatedOn);
        AddIfChanged(previous, "Modified", undo.ContentModifiedOn, redo.ContentModifiedOn);
        AddIfChanged(previous, "Record changed", undo.RecordModifiedOn, redo.RecordModifiedOn);
        AddIfChanged(previous, "Accessed", undo.LastAccessedOn, redo.LastAccessedOn);
        if (undo.RealSize != redo.RealSize)
        {
            previous.Add($"Size {undo.RealSize}");
        }

        if (previous.Count > 0)
        {
            row.Details += $"; previous: {string.Join(", ", previous)}";
        }
    }

    /// <summary>
    ///     An update of $STANDARD_INFORMATION timestamps (such as a time stomp) is an UpdateResidentValue of the first
    ///     attribute; anything else stays raw
    /// </summary>
    private void SetResidentValue(LogFileOut row, NtfsLogRecord ntfs)
    {
        if (ntfs.TargetMftEntry.HasValue == false || ntfs.RecordOffset != FirstAttributeOffset(ntfs.TargetMftEntry.Value) ||
            ntfs.AttributeOffset < ResidentHeaderSize)
        {
            return;
        }

        var valueOffset = ntfs.AttributeOffset - ResidentHeaderSize;

        var changed = new List<string>();
        var previous = new List<string>();
        var values = new DateTimeOffset?[4];
        for (var i = 0; i < 4; i++)
        {
            var at = i * 8 - valueOffset;
            if (at < 0 || at + 8 > ntfs.RedoData.Length)
            {
                continue;
            }

            values[i] = FileTime(ntfs.RedoData, at);
            changed.Add(SiTimestampNames[i]);

            if (at + 8 <= ntfs.UndoData.Length)
            {
                previous.Add($"{SiTimestampNames[i]} {FileTime(ntfs.UndoData, at)?.ToString("yyyy-MM-dd HH:mm:ss.fffffff") ?? "(none)"}");
            }
        }

        if (changed.Count == 0)
        {
            return;
        }

        row.AttributeType = nameof(MFT.Attributes.AttributeType.StandardInformation);
        row.CreatedOn = values[0];
        row.ContentModifiedOn = values[1];
        row.RecordModifiedOn = values[2];
        row.LastAccessedOn = values[3];
        row.EntryNumber = (ulong)ntfs.TargetMftEntry.Value;
        row.Details = $"$STANDARD_INFORMATION timestamps changed: {string.Join(", ", changed)}";
        if (previous.Count > 0)
        {
            row.Details += $"; previous: {string.Join(", ", previous)}";
        }
    }

    private int FirstAttributeOffset(long entry)
    {
        if (_byEntry.TryGetValue(entry, out var record) && record.FirstAttributeOffset > 0)
        {
            return record.FirstAttributeOffset;
        }

        return _firstAttributeOffset;
    }

    private void SetFileInfo(LogFileOut row, FileInfo fileInfo)
    {
        row.FileName = fileInfo.FileName;
        row.ParentEntryNumber = fileInfo.ParentMftRecord.MftEntryNumber;
        row.ParentSequenceNumber = fileInfo.ParentMftRecord.MftSequenceNumber;
        row.CreatedOn = fileInfo.CreatedOn;
        row.ContentModifiedOn = fileInfo.ContentModifiedOn;
        row.RecordModifiedOn = fileInfo.RecordModifiedOn;
        row.LastAccessedOn = fileInfo.LastAccessedOn;
        row.FileSize = fileInfo.LogicalSize;
        row.FileAttributes = Flags(fileInfo.Flags);

        if (_mft != null)
        {
            row.ParentPath = _mft.GetFullParentPath(fileInfo.ParentMftRecord.GetKey());
        }
    }

    private static void SetStandardInfo(LogFileOut row, StandardInfo si)
    {
        //$STANDARD_INFORMATION wins over $FILE_NAME (as in the $MFT output)
        row.CreatedOn = si.CreatedOn;
        row.ContentModifiedOn = si.ContentModifiedOn;
        row.RecordModifiedOn = si.RecordModifiedOn;
        row.LastAccessedOn = si.LastAccessedOn;
        row.FileAttributes = Flags(si.Flags);
    }

    private string TargetPath(long entry)
    {
        if (_mft == null)
        {
            return null;
        }

        if (_targetPaths.TryGetValue(entry, out var path))
        {
            return path;
        }

        path = null;
        if (_byEntry.TryGetValue(entry, out var record))
        {
            path = entry == 5 ? "." : PathOf(record);
        }

        _targetPaths[entry] = path;
        return path;
    }

    private static void AddIfChanged(List<string> previous, string name, DateTimeOffset? before, DateTimeOffset? after)
    {
        if (before != after)
        {
            previous.Add($"{name} {before?.ToString("yyyy-MM-dd HH:mm:ss.fffffff") ?? "(none)"}");
        }
    }

    private static string Flags(StandardInfo.Flag flags) => flags.ToString().Replace(", ", "|");

    private static DateTimeOffset? FileTime(byte[] data, int offset)
    {
        var raw = BitConverter.ToInt64(data, offset);
        if (raw <= 0 || raw > 2650467743999999999)
        {
            return null;
        }

        return DateTimeOffset.FromFileTime(raw).ToUniversalTime();
    }
}
