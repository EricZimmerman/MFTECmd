using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MFTECmd
{
  public  class BodyFile
    {
        public int Md5 { get; set; }
        public string Name { get; set; }
        public string Inode { get; set; }
        public string Mode { get; set; }
        public int Uid { get; set; }
        public int Gid { get; set; }
        public ulong Size { get; set; }
        public long AccessTime { get; set; }
        public long ModifiedTime { get; set; }
        public long RecordModifiedTime { get; set; }
        public long CreatedTime { get; set; }
        

    }
}
