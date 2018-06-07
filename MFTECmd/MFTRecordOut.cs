using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MFT;

namespace MFTECmd
{
   public class MFTRecordOut
    {
        public uint EntryNumber { get; set; }
        public ushort SequenceNumber { get; set; }
        public bool InUse { get; set; }
        public string ParentPath { get; set; }
        public string FileName { get; set; }



    }

    
}
