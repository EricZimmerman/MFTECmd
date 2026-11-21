using CsvHelper;

namespace MFTECmd;

[CsvRecord]
public partial class BootOut
{
    public string EntryPoint { get; set; }
    public string Signature { get; set; }
    public int BytesPerSector { get; set; }
    public int SectorsPerCluster { get; set; }
    /// <summary>BytesPerSector * SectorsPerCluster (settable: CsvHelper 34 writes only members it can set)</summary>
    public int ClusterSize { get; set; }
    public long ReservedSectors { get; set; }
    public long TotalSectors { get; set; }
    public long MftClusterBlockNumber { get; set; }
    public long MftMirrClusterBlockNumber { get; set; }
    public int MftEntrySize { get; set; }
    public int IndexEntrySize { get; set; }
    public string VolumeSerialNumberRaw { get; set; }
    public string VolumeSerialNumber { get; set; }
    public string VolumeSerialNumber32 { get; set; }
    public string VolumeSerialNumber32Reverse { get; set; }
    public string SectorSignature { get; set; }
    public string SourceFile { get; set; }
}