using System.Globalization;
using Microsoft.AspNetCore.Hosting;

namespace SelfOrderingSystemKiosk.Services
{
    /// <summary>
    /// Maps menu items to photos under wwwroot/images/Kp items (filename pattern "Kp … .jpg").
    /// When a file matches the DB item, the visible menu name comes from that filename (without the "Kp " prefix).
    /// </summary>
    public class KpItemsImageResolver
    {
        private const string FolderSegment = "Kp items";
        private const double MinMatchScore = 0.38;
        private readonly List<(string StemNorm, string RelPath, string DisplayStem)> _entries = new();

        public KpItemsImageResolver(IWebHostEnvironment env)
        {
            var dir = Path.Combine(env.WebRootPath, "images", FolderSegment);
            if (!Directory.Exists(dir))
                return;

            foreach (var full in Directory.GetFiles(dir))
            {
                var ext = Path.GetExtension(full);
                if (!ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".webp", StringComparison.OrdinalIgnoreCase))
                    continue;

                var fileName = Path.GetFileName(full);
                var stem = Path.GetFileNameWithoutExtension(fileName);
                if (stem.StartsWith("Kp ", StringComparison.OrdinalIgnoreCase))
                    stem = stem[3..];
                else if (stem.StartsWith("Kp", StringComparison.OrdinalIgnoreCase) && stem.Length > 2 && stem[2] == ' ')
                    stem = stem[3..];

                stem = stem.Trim();
                var stemNorm = Normalize(stem);
                if (string.IsNullOrEmpty(stemNorm))
                    continue;

                var encodedName = Uri.EscapeDataString(fileName);
                var relPath = "/images/" + Uri.EscapeDataString(FolderSegment) + "/" + encodedName;

                _entries.Add((stemNorm, relPath, stem));
            }
        }

        /// <summary>Menu title from the matched image filename (title case, no "Kp " prefix), or the DB name if no match.</summary>
        public string ResolveMenuLabel(string? menuItemNameFromDb)
        {
            var m = FindBestMatch(menuItemNameFromDb);
            if (m != null && m.Score >= MinMatchScore)
                return ToMenuTitleWords(m.DisplayStem);
            return string.IsNullOrWhiteSpace(menuItemNameFromDb) ? "" : menuItemNameFromDb.Trim();
        }

        /// <summary>Image path and customer-facing label for kiosk / lists.</summary>
        public (string Label, string ImagePath) ResolveForMenu(string? menuItemNameFromDb, string? existingImage, string defaultImage = "/images/wings.png")
        {
            var m = FindBestMatch(menuItemNameFromDb);
            if (m != null && m.Score >= MinMatchScore)
                return (ToMenuTitleWords(m.DisplayStem), m.RelPath);

            var img = string.IsNullOrWhiteSpace(existingImage) ? defaultImage : existingImage;
            var label = string.IsNullOrWhiteSpace(menuItemNameFromDb) ? "" : menuItemNameFromDb.Trim();
            return (label, img);
        }

        /// <summary>Returns a web path when a confident match exists; otherwise null.</summary>
        public string? TryResolvePath(string? menuItemName)
        {
            var m = FindBestMatch(menuItemName);
            return m != null && m.Score >= MinMatchScore ? m.RelPath : null;
        }

        /// <summary>Prefer KP folder photo when it matches; otherwise fall back to stored image or default.</summary>
        public string ResolveDisplayPath(string? menuItemName, string? existingImage, string defaultPath = "/images/wings.png")
        {
            var kp = TryResolvePath(menuItemName);
            if (!string.IsNullOrEmpty(kp))
                return kp;
            if (!string.IsNullOrWhiteSpace(existingImage))
                return existingImage;
            return defaultPath;
        }

        private sealed class MatchCandidate
        {
            public required string RelPath { get; init; }
            public required string DisplayStem { get; init; }
            public double Score { get; init; }
        }

        private MatchCandidate? FindBestMatch(string? menuItemName)
        {
            if (string.IsNullOrWhiteSpace(menuItemName) || _entries.Count == 0)
                return null;

            var itemNorm = Normalize(menuItemName);
            if (string.IsNullOrEmpty(itemNorm))
                return null;

            MatchCandidate? best = null;
            var bestStemLen = 0;

            foreach (var (stemNorm, relPath, displayStem) in _entries)
            {
                var s = ScoreTokens(itemNorm, stemNorm);
                if (s > (best?.Score ?? 0) + 0.0001
                    || (Math.Abs(s - (best?.Score ?? 0)) < 0.0001 && stemNorm.Length > bestStemLen))
                {
                    best = new MatchCandidate { RelPath = relPath, DisplayStem = displayStem, Score = s };
                    bestStemLen = stemNorm.Length;
                }
            }

            return best;
        }

        private static string ToMenuTitleWords(string stem)
        {
            if (string.IsNullOrWhiteSpace(stem))
                return "";

            var collapsed = string.Join(" ", stem.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(collapsed.ToLowerInvariant());
        }

        private static string Normalize(string s)
        {
            var lower = s.ToLowerInvariant().Trim();
            var chars = lower.Select(c => char.IsLetterOrDigit(c) || c == ' ' ? c : ' ').ToArray();
            var collapsed = new string(chars);
            while (collapsed.Contains("  ", StringComparison.Ordinal))
                collapsed = collapsed.Replace("  ", " ", StringComparison.Ordinal);
            return collapsed.Trim();
        }

        private static HashSet<string> ExpandTokens(string norm)
        {
            var raw = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (raw.Contains("wing"))
                raw.Add("wings");
            if (raw.Contains("wings"))
                raw.Add("wing");
            return raw;
        }

        private static double ScoreTokens(string itemNorm, string stemNorm)
        {
            var a = ExpandTokens(itemNorm);
            var b = ExpandTokens(stemNorm);
            if (a.Count == 0 || b.Count == 0)
                return 0;

            var inter = a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();
            var union = a.Union(b, StringComparer.OrdinalIgnoreCase).Count();
            var jaccard = (double)inter / union;

            var subsetBonus = 0d;
            if (a.IsSubsetOf(b) || b.IsSubsetOf(a))
                subsetBonus = 0.18;

            var containsBonus = 0d;
            if (itemNorm.Contains(stemNorm, StringComparison.OrdinalIgnoreCase)
                || stemNorm.Contains(itemNorm, StringComparison.OrdinalIgnoreCase))
                containsBonus = 0.12;

            return Math.Min(1, jaccard + subsetBonus + containsBonus);
        }
    }
}
