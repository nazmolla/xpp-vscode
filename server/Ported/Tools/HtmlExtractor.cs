using System.Text;

namespace XppVsCode.Server.Tools
{
    /// <summary>
    /// Extracts readable text from HTML pages (primarily MS Learn docs).
    /// </summary>
    internal static class HtmlExtractor
    {
        public static string ExtractMainContent(string html)
        {
            if (string.IsNullOrEmpty(html)) return null;

            string content = null;

            // MS Learn uses <main ...>...</main>
            int mainStart = html.IndexOf("<main", System.StringComparison.OrdinalIgnoreCase);
            if (mainStart >= 0)
            {
                int mainClose = html.IndexOf(">", mainStart) + 1;
                int mainEnd = html.IndexOf("</main>", mainClose, System.StringComparison.OrdinalIgnoreCase);
                if (mainEnd > mainClose)
                    content = html.Substring(mainClose, mainEnd - mainClose);
            }

            // Fallback: <article>
            if (content == null)
            {
                int artStart = html.IndexOf("<article", System.StringComparison.OrdinalIgnoreCase);
                if (artStart >= 0)
                {
                    int artClose = html.IndexOf(">", artStart) + 1;
                    int artEnd = html.IndexOf("</article>", artClose, System.StringComparison.OrdinalIgnoreCase);
                    if (artEnd > artClose)
                        content = html.Substring(artClose, artEnd - artClose);
                }
            }

            if (content == null) return null;

            content = RemoveHtmlBlock(content, "nav");
            content = RemoveHtmlBlock(content, "header");
            content = RemoveHtmlBlock(content, "footer");
            content = RemoveHtmlBlock(content, "aside");
            content = RemoveHtmlBlock(content, "script");
            content = RemoveHtmlBlock(content, "style");

            string text = StripHtmlTags(content);
            return NormalizeWhitespace(text);
        }

        public static string StripHtmlTags(string html)
        {
            if (html == null) return "";
            var sb = new StringBuilder(html.Length);
            bool inTag = false;
            bool inEntity = false;
            var entity = new StringBuilder();
            for (int i = 0; i < html.Length; i++)
            {
                char c = html[i];
                if (c == '<') { inTag = true; continue; }
                if (c == '>' && inTag) { inTag = false; sb.Append(' '); continue; }
                if (inTag) continue;

                if (c == '&')
                {
                    inEntity = true;
                    entity.Clear();
                    entity.Append(c);
                    continue;
                }
                if (inEntity)
                {
                    entity.Append(c);
                    if (c == ';')
                    {
                        string ent = entity.ToString();
                        switch (ent)
                        {
                            case "&amp;": sb.Append('&'); break;
                            case "&lt;": sb.Append('<'); break;
                            case "&gt;": sb.Append('>'); break;
                            case "&quot;": sb.Append('"'); break;
                            case "&apos;": sb.Append('\''); break;
                            case "&nbsp;": sb.Append(' '); break;
                            case "&#39;": sb.Append('\''); break;
                            default: sb.Append(ent); break;
                        }
                        inEntity = false;
                    }
                    else if (entity.Length > 10)
                    {
                        sb.Append(entity);
                        inEntity = false;
                    }
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string RemoveHtmlBlock(string html, string tag)
        {
            if (html == null) return null;
            string openTag = "<" + tag;
            string closeTag = "</" + tag + ">";
            int pos = 0;
            var sb = new StringBuilder(html.Length);
            while (pos < html.Length)
            {
                int start = html.IndexOf(openTag, pos, System.StringComparison.OrdinalIgnoreCase);
                if (start < 0)
                {
                    sb.Append(html, pos, html.Length - pos);
                    break;
                }
                sb.Append(html, pos, start - pos);
                int end = html.IndexOf(closeTag, start, System.StringComparison.OrdinalIgnoreCase);
                if (end < 0) break;
                pos = end + closeTag.Length;
            }
            return sb.ToString();
        }

        private static string NormalizeWhitespace(string text)
        {
            if (text == null) return "";
            var sb = new StringBuilder(text.Length);
            bool lastWasSpace = false;
            int consecutiveNewlines = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') continue;
                    consecutiveNewlines++;
                    if (consecutiveNewlines <= 2) sb.Append('\n');
                    lastWasSpace = true;
                    continue;
                }
                if (c == ' ' || c == '\t')
                {
                    if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
                    continue;
                }
                consecutiveNewlines = 0;
                lastWasSpace = false;
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }
}
