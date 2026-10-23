using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace XppVsCode.Server.Tools
{
    /// <summary>
    /// The full agent guide (rules with examples, code templates, language and object references), served by topic so the
    /// always-loaded instructions can stay short.
    /// </summary>
    internal static class AgentGuide
    {
        private const int MaxChars = 24000;
        private static List<KeyValuePair<string, string>> _sections;

        /// <summary>Sections of the guide: "== TITLE ==" headings, and PHASE / boxed headings inside them.</summary>
        private static List<KeyValuePair<string, string>> Sections(string resourceName)
        {
            if (_sections != null) return _sections;
            string text;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null) return _sections = new List<KeyValuePair<string, string>>();
                using (var reader = new StreamReader(stream, Encoding.UTF8)) text = reader.ReadToEnd();
            }
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var sections = new List<KeyValuePair<string, string>>();
            string title = "Introduction";
            var body = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                bool top = line.StartsWith("== ") && line.EndsWith(" ==");
                // A boxed heading: a rule line, the title, a rule line.
                bool boxed = line.Length > 0 && !line.StartsWith("─") && i > 0 && i + 1 < lines.Length
                    && lines[i - 1].Trim().StartsWith("────") && lines[i + 1].Trim().StartsWith("────");
                if (top || boxed)
                {
                    if (body.ToString().Trim().Length > 0) sections.Add(new KeyValuePair<string, string>(title, body.ToString().Trim()));
                    title = top ? line.Trim('=', ' ') : line;
                    body.Clear();
                    if (boxed) i++;   // skip the closing rule line
                    continue;
                }
                if (line.StartsWith("────") && i + 2 < lines.Length && lines[i + 2].Trim().StartsWith("────")) continue;
                body.AppendLine(lines[i]);
            }
            if (body.ToString().Trim().Length > 0) sections.Add(new KeyValuePair<string, string>(title, body.ToString().Trim()));
            return _sections = sections;
        }

        public static string Get(string resourceName, string topic)
        {
            var sections = Sections(resourceName);
            if (sections.Count == 0) return "The guide is not available in this build.";
            if (string.IsNullOrWhiteSpace(topic))
                return "Guide topics (call xpp_get_guide with a topic, words of a title are enough):\n"
                    + string.Join("\n", sections.Select(s => "- " + s.Key + " (" + s.Value.Length / 1000 + "k chars)"));

            var words = topic.ToLowerInvariant().Split(new[] { ' ', ',', '/', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            // Titles with every word first; otherwise sections whose text has every word, most mentions first.
            var hits = sections.Where(s => words.All(w => s.Key.ToLowerInvariant().Contains(w))).ToList();
            if (hits.Count == 0)
                hits = sections.Select(s => new { s, n = words.Sum(w => Count(s.Value.ToLowerInvariant(), w)) })
                    .Where(x => words.All(w => x.s.Value.ToLowerInvariant().Contains(w)))
                    .OrderByDescending(x => x.n).Take(2).Select(x => x.s).ToList();
            if (hits.Count == 0)
                return "No guide section matches '" + topic + "'. Topics:\n" + string.Join("\n", sections.Select(s => "- " + s.Key));
            var sb = new StringBuilder();
            foreach (var s in hits)
            {
                if (sb.Length > 0 && sb.Length + s.Value.Length > MaxChars)
                {
                    sb.AppendLine("\n(More sections match: " + string.Join(", ", hits.SkipWhile(h => h.Key != s.Key).Select(h => h.Key)) + ". Ask for one by title.)");
                    break;
                }
                sb.AppendLine("== " + s.Key + " ==").AppendLine(s.Value.Length > MaxChars ? s.Value.Substring(0, MaxChars) + "\n[truncated]" : s.Value).AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        private static int Count(string text, string word)
        {
            int n = 0, at = 0;
            while ((at = text.IndexOf(word, at, StringComparison.Ordinal)) >= 0) { n++; at += word.Length; }
            return n;
        }
    }
}
