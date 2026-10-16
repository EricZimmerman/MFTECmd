using System.Collections.Generic;
using System.Linq;
using MFT;
using MFT.Attributes;
using MFT.Other;
using Usn;

namespace MFTECmd;

/// <summary>
///     Parent paths for $J records as they were when each record was written, by "rewinding" the journal: start from
///     the directories in the $MFT (the state at the end of the journal), then walk the records from newest to oldest.
///     A RenameOldName record gives the name and parent an entry had before that rename, and any record of an entry
///     that is not known yet (such as the FileDelete of a directory whose $MFT record was reused) gives its name and
///     parent at that time. Paths through deleted, reused or renamed directories are then resolved, where the $MFT
///     alone gives PathUnknown or the current location.
///     <para>
///         Technique and proof of concept (usnjrnl_rewind, MIT) by Yogesh Khatri, CyberCX:
///         https://cybercx.com.au/blog/ntfs-usnjrnl-rewind/
///     </para>
/// </summary>
public static class UsnRewind
{
    private const ulong RootKey = (5UL << 16) | 5;

    private static ulong Key(ulong entry, uint sequence) => (entry << 16) | (sequence & 0xFFFF);

    private static string KeyString(ulong key) => $"{key >> 16:X8}-{key & 0xFFFF:X8}";

    /// <summary>The parent path for each of <paramref name="entries" />, in the same order</summary>
    public static string[] GetParentPaths(Mft mft, IReadOnlyList<UsnEntry> entries)
    {
        var state = new Dictionary<ulong, (string Name, ulong Parent)>();

        foreach (var record in mft.FileRecords.Values.Concat(mft.FreeFileRecords.Values))
        {
            if (record.MftRecordToBaseRecord.MftEntryNumber > 0 && record.MftRecordToBaseRecord.MftSequenceNumber > 0)
            {
                //extension records; their attributes are in the base record
                continue;
            }

            if (record.IsDirectory() == false)
            {
                //only directories are parents
                continue;
            }

            var fn = record.Attributes.OfType<FileName>().FirstOrDefault(t => t.FileInfo.NameType != NameTypes.Dos);
            if (fn == null)
            {
                continue;
            }

            //a free record's sequence number was incremented when it was freed, so its last occupant had the one before
            var sequence = record.IsDeleted() ? (uint)(ushort)(record.SequenceNumber - 1) : record.SequenceNumber;
            var parent = fn.FileInfo.ParentMftRecord;
            state[Key(record.EntryNumber, sequence)] = (fn.FileInfo.FileName, Key(parent.MftEntryNumber, parent.MftSequenceNumber));
        }

        //paths are cached by parent key until a change touches a key one of them used, or adds a key one of them lacked
        var cache = new Dictionary<ulong, string>();
        var usedKeys = new HashSet<ulong>();
        var missingKeys = new HashSet<ulong>();

        void Changed(ulong key)
        {
            if (usedKeys.Contains(key) || missingKeys.Contains(key))
            {
                cache.Clear();
                usedKeys.Clear();
                missingKeys.Clear();
            }
        }

        string Resolve(ulong parentKey)
        {
            if (cache.TryGetValue(parentKey, out var cached))
            {
                return cached;
            }

            var names = new List<string>();
            var seen = new HashSet<ulong>();
            var key = parentKey;
            string path = null;

            while (true)
            {
                if (key == RootKey)
                {
                    usedKeys.Add(key);
                    names.Add(".");
                    break;
                }

                if (seen.Add(key) == false || state.TryGetValue(key, out var value) == false)
                {
                    //not known (or a loop): the same form as Mft.GetFullParentPath
                    missingKeys.Add(key);
                    var known = mft.GetFullParentPath(KeyString(key));
                    names.Add(known.Contains("PathUnknown") ? $".\\PathUnknown\\Directory with ID 0x{KeyString(key)}" : known);
                    break;
                }

                usedKeys.Add(key);
                names.Add(value.Name);
                key = value.Parent;
            }

            names.Reverse();
            path = string.Join("\\", names);
            cache[parentKey] = path;
            return path;
        }

        var paths = new string[entries.Count];

        //records are in USN order in the file; walk them newest first
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            var key = Key(e.FileReference.EntryNumber, e.FileReference.SequenceNumber);
            var parentKey = Key(e.ParentFileReference.EntryNumber, e.ParentFileReference.SequenceNumber);

            if ((e.UpdateReasons & UsnEntry.UpdateReasonFlag.RenameOldName) == UsnEntry.UpdateReasonFlag.RenameOldName)
            {
                //before this rename, the entry had this name and parent
                if (state.TryGetValue(key, out var old) == false || old.Name != e.Name || old.Parent != parentKey)
                {
                    state[key] = (e.Name, parentKey);
                    Changed(key);
                }
            }
            else if (state.ContainsKey(key) == false)
            {
                //the newest record of an entry the $MFT does not have (deleted, and the record reused)
                state[key] = (e.Name, parentKey);
                Changed(key);
            }

            paths[i] = Resolve(parentKey);
        }

        return paths;
    }
}
