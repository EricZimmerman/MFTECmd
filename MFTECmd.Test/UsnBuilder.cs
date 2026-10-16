using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Usn;

namespace MFTECmd.Test;

/// <summary>Builds a $J of USN_RECORD_V2 records, in the order they are added (the USN is the offset)</summary>
public class UsnBuilder
{
    private const uint Directory = 0x10;
    private const uint Archive = 0x20;

    private readonly MemoryStream _data = new();
    private DateTimeOffset _time = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public UsnBuilder Add(string name, ulong entry, ushort sequence, ulong parentEntry, ushort parentSequence,
        UsnEntry.UpdateReasonFlag reasons, bool isDirectory = false)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var length = (60 + nameBytes.Length + 7) / 8 * 8;

        var record = new byte[length];
        BitConverter.GetBytes(length).CopyTo(record, 0);
        BitConverter.GetBytes((short)2).CopyTo(record, 4);
        BitConverter.GetBytes(entry | ((ulong)sequence << 48)).CopyTo(record, 8);
        BitConverter.GetBytes(parentEntry | ((ulong)parentSequence << 48)).CopyTo(record, 16);
        BitConverter.GetBytes(_data.Length).CopyTo(record, 24);
        BitConverter.GetBytes(_time.ToFileTime()).CopyTo(record, 32);
        BitConverter.GetBytes((uint)reasons).CopyTo(record, 40);
        BitConverter.GetBytes(isDirectory ? Directory : Archive).CopyTo(record, 52);
        BitConverter.GetBytes((short)nameBytes.Length).CopyTo(record, 56);
        BitConverter.GetBytes((short)60).CopyTo(record, 58);
        nameBytes.CopyTo(record, 60);

        _data.Write(record, 0, record.Length);
        _time = _time.AddSeconds(1);
        return this;
    }

    public byte[] ToArray() => _data.ToArray();

    public List<UsnEntry> Entries()
    {
        var bytes = ToArray();
        return new Usn.Usn(new MemoryStream(bytes), 0).UsnEntries;
    }
}
