using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FastbootEnhance.Core.Images
{
    /// <summary>Names of images that come in numbered parts.</summary>
    public static class ImageNaming
    {
        // "super.img.3", "system.img_sparsechunk.12", "super_7.img"
        static readonly Regex TrailingNumber = new Regex(@"\.(\d+)$");
        static readonly Regex UnderscoreNumber = new Regex(@"_(\d+)\.[A-Za-z0-9]+$");

        /// <summary>The part number in a file name, or -1.</summary>
        public static long PartNumber(string path)
        {
            string name = System.IO.Path.GetFileName(path);
            Match match = TrailingNumber.Match(name);
            if (!match.Success)
                match = UnderscoreNumber.Match(name);
            long number;
            return match.Success && long.TryParse(match.Groups[1].Value, out number) ? number : -1;
        }

        /// <summary>Puts parts in the order of their numbers (2 before 10), then by name.</summary>
        public static IList<string> OrderParts(IEnumerable<string> paths)
        {
            return paths.OrderBy(PartNumber).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
