using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace Comgenie.Util
{
    public static class StringExtensions
    {
        public static string? Between(this string txt, string start, string end)
        {
            var pos = txt.IndexOf(start);
            if (pos < 0)
                return null;
            txt = txt.Substring(pos + start.Length);
            pos = txt.IndexOf(end);
            if (pos < 0)
                return null;
            return txt.Substring(0, pos);
        }
    }
}
