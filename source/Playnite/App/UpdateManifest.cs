using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Playnite
{
    public class ReleaseNoteData
    {
        public Version Version
        {
            get; set;
        }

        /// <summary>How the version is shown: «1.12.3-beta».</summary>
        public string Label
        {
            get; set;
        }

        public string Note
        {
            get; set;
        }
    }
}
