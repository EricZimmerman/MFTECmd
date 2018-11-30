using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MFTECmd
{
  public  class BootOut
  {
      public string EntryPoint { get; set; }
      public string Signature { get; set; }
      public int BytesPerSector { get; set; }
      public int SectorsPerCluster { get; set; }
      public int ClusterSize => BytesPerSector * SectorsPerCluster;
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

  }
}
