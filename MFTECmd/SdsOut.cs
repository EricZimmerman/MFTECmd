using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MFTECmd
{
   public class SdsOut
    {
        public string Hash { get; set; }
        public int Id { get; set; }
        public long Offset { get; set; }
        public string OwnerSid { get; set; }
        public string GroupSid { get; set; }
    }
}
