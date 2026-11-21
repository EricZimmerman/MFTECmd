using System;
using CsvHelper;

namespace MFTECmd;

/// <summary>CsvHelper 34 options, converters and record writers that keep the CSVs as CsvHelper 33 wrote them</summary>
internal static class CsvOutput
{
    /// <summary>The --dt format, set once at startup, for <see cref="DateConverter" /> and <see cref="NullableDateConverter" /></summary>
    internal static string DateTimeFormat = "yyyy-MM-dd HH:mm:ss.fffffff";

    /// <summary>
    ///     CsvHelper 33's output: True/False, CRLF, and fields that start or end with a space quoted (spaces only: 33
    ///     did not quote a field for a tab there, which CsvQuoting.WhenNeededOrWhitespace does)
    /// </summary>
    internal static CsvWriterOptions Options(bool byteOrderMark) => new()
    {
        ShouldQuote = (field, fieldType, fieldIndex, recordIndex) =>
            field.Length > 0 && (field[0] == ' ' || field[field.Length - 1] == ' '),
        TrueValue = "True",
        FalseValue = "False",
        WriteByteOrderMark = byteOrderMark
    };

    /// <summary>A timestamp in the --dt format (an empty field when null), as the old ClassMap's Convert lambdas wrote it</summary>
    internal static string Format(DateTimeOffset? value) => $"{value?.ToString(DateTimeFormat)}";
}

/// <summary>Writes a <see cref="DateTimeOffset" /> in the --dt format</summary>
public sealed class DateConverter : ICsvConverter<DateTimeOffset>
{
    public DateTimeOffset Read(CsvReader reader, int index) =>
        DateTimeOffset.ParseExact(reader.GetString(index), CsvOutput.DateTimeFormat, null);

    public void Write(CsvWriter writer, DateTimeOffset value) => writer.WriteField(CsvOutput.Format(value));
}

/// <summary>Writes a <see cref="DateTimeOffset" />? in the --dt format, null as an empty field</summary>
public sealed class NullableDateConverter : ICsvConverter<DateTimeOffset?>
{
    public DateTimeOffset? Read(CsvReader reader, int index)
    {
        var text = reader.GetString(index);
        return text.Length == 0 ? null : DateTimeOffset.ParseExact(text, CsvOutput.DateTimeFormat, null);
    }

    public void Write(CsvWriter writer, DateTimeOffset? value) => writer.WriteField(CsvOutput.Format(value));
}

/// <summary>
///     The $MFT CSV: the columns in their fixed order, the three ResidentData* columns only with --ir (issue #58),
///     ParseErrors and ResidentDataDecoded last
/// </summary>
internal sealed class MftRecordWriter : CsvRecordWriter<MFTRecordOut>
{
    private static readonly string[] Leading =
    {
        "EntryNumber", "SequenceNumber", "InUse", "ParentEntryNumber", "ParentSequenceNumber", "ParentPath", "FileName",
        "Extension", "FileSize", "ReferenceCount", "ReparseTarget", "IsDirectory", "HasAds", "IsAds", "SI<FN", "uSecZeros",
        "Copied", "SiFlags", "NameType", "Created0x10", "Created0x30", "LastModified0x10", "LastModified0x30",
        "LastRecordChange0x10", "LastRecordChange0x30", "LastAccess0x10", "LastAccess0x30", "UpdateSequenceNumber",
        "LogfileSequenceNumber", "SecurityId", "ObjectIdFileDroid", "LoggedUtilStream", "ZoneIdContents", "SourceFile"
    };

    private static readonly string[] Resident = { "ResidentDataBase64", "ResidentDataHex", "ResidentDataASCII" };

    private static readonly string[] Trailing = { "ParseErrors", "ResidentDataDecoded" };

    private readonly bool _includeResident;

    public MftRecordWriter(bool includeResident)
    {
        _includeResident = includeResident;
    }

    public override int FieldCount => Leading.Length + (_includeResident ? Resident.Length : 0) + Trailing.Length;

    public override void WriteHeader(CsvWriter writer)
    {
        foreach (var name in Leading)
        {
            WriteColumnName(writer, name);
        }

        if (_includeResident)
        {
            foreach (var name in Resident)
            {
                WriteColumnName(writer, name);
            }
        }

        foreach (var name in Trailing)
        {
            WriteColumnName(writer, name);
        }
    }

    public override void Write(CsvWriter writer, MFTRecordOut r)
    {
        writer.WriteField(r.EntryNumber);
        writer.WriteField(r.SequenceNumber);
        writer.WriteField(r.InUse);
        writer.WriteField(r.ParentEntryNumber);
        writer.WriteField(r.ParentSequenceNumber);
        writer.WriteField(r.ParentPath);
        writer.WriteField(r.FileName);
        writer.WriteField(r.Extension);
        writer.WriteField(r.FileSize);
        writer.WriteField(r.ReferenceCount);
        writer.WriteField(r.ReparseTarget);
        writer.WriteField(r.IsDirectory);
        writer.WriteField(r.HasAds);
        writer.WriteField(r.IsAds);
        writer.WriteField(r.Timestomped);
        writer.WriteField(r.uSecZeros);
        writer.WriteField(r.Copied);
        writer.WriteField(r.SiFlags.ToString().Replace(", ", "|"));
        writer.WriteField(r.NameType.ToString());
        writer.WriteField(CsvOutput.Format(r.Created0x10));
        writer.WriteField(CsvOutput.Format(r.Created0x30));
        writer.WriteField(CsvOutput.Format(r.LastModified0x10));
        writer.WriteField(CsvOutput.Format(r.LastModified0x30));
        writer.WriteField(CsvOutput.Format(r.LastRecordChange0x10));
        writer.WriteField(CsvOutput.Format(r.LastRecordChange0x30));
        writer.WriteField(CsvOutput.Format(r.LastAccess0x10));
        writer.WriteField(CsvOutput.Format(r.LastAccess0x30));
        writer.WriteField(r.UpdateSequenceNumber);
        writer.WriteField(r.LogfileSequenceNumber);
        writer.WriteField(r.SecurityId);
        writer.WriteField(r.ObjectIdFileDroid);
        writer.WriteField(r.LoggedUtilStream);
        writer.WriteField(r.ZoneIdContents);
        writer.WriteField(r.SourceFile);

        if (_includeResident)
        {
            writer.WriteField(r.ResidentDataBase64);
            writer.WriteField(r.ResidentDataHex);
            writer.WriteField(r.ResidentDataASCII);
        }

        writer.WriteField(r.ParseErrors);
        writer.WriteField(r.ResidentDataDecoded);
    }
}
