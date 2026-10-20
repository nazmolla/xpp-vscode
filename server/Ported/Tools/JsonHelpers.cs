using System;
using System.Collections.Generic;
using System.Text;

namespace XppVsCode.Server.Tools
{
    /// <summary>
    /// Minimal JSON parsing and formatting — no external dependencies.
    /// </summary>
    internal static class JsonHelpers
    {
        public static string ExtractJsonValueToken(string json, string key)
        {
            if (json == null) return null;
            string pattern = "\"" + key + "\"";

            // Search only at the top level (depth 1) — same guard as ExtractJsonString.
            // Prevents matching a same-named key that appears as a value deeper in the tree
            // (e.g. a field whose "name" value happens to equal "id" would otherwise corrupt
            // the JSON-RPC id extraction and send the response to the wrong caller).
            int searchFrom = 0;
            int idx = -1;
            while (true)
            {
                idx = json.IndexOf(pattern, searchFrom, StringComparison.Ordinal);
                if (idx < 0) return null;

                int depth = 0;
                bool inStr = false;
                bool esc = false;
                for (int i = 0; i < idx; i++)
                {
                    char ch = json[i];
                    if (esc) { esc = false; continue; }
                    if (ch == '\\') { esc = true; continue; }
                    if (ch == '"') { inStr = !inStr; continue; }
                    if (inStr) continue;
                    if (ch == '{' || ch == '[') depth++;
                    else if (ch == '}' || ch == ']') depth--;
                }

                if (depth <= 1) break;
                searchFrom = idx + pattern.Length;
            }

            int colonIdx = json.IndexOf(':', idx + pattern.Length);
            if (colonIdx < 0) return null;

            int valStart = colonIdx + 1;
            while (valStart < json.Length && char.IsWhiteSpace(json[valStart])) valStart++;
            if (valStart >= json.Length) return null;

            if (json[valStart] == '"')
            {
                bool escape = false;
                for (int i = valStart + 1; i < json.Length; i++)
                {
                    char c = json[i];
                    if (escape) { escape = false; continue; }
                    if (c == '\\') { escape = true; continue; }
                    if (c == '"') return json.Substring(valStart, i - valStart + 1);
                }
                return null;
            }

            int end = valStart;
            while (end < json.Length)
            {
                char c = json[end];
                if (c == ',' || c == '}' || c == ']' || char.IsWhiteSpace(c)) break;
                end++;
            }
            if (end <= valStart) return null;
            return json.Substring(valStart, end - valStart);
        }

        public static string ExtractJsonString(string json, string key)
        {
            if (json == null) return null;
            string pattern = "\"" + key + "\"";

            // Search for the key only at the top level of the JSON object (depth 1).
            // This prevents matching nested keys like "name" inside arrays/objects.
            int searchFrom = 0;
            int idx = -1;
            while (true)
            {
                idx = json.IndexOf(pattern, searchFrom, StringComparison.Ordinal);
                if (idx < 0) return null;

                // Calculate the depth at this position by counting unmatched braces/brackets
                int depth = 0;
                bool inStr = false;
                bool esc = false;
                for (int i = 0; i < idx; i++)
                {
                    char ch = json[i];
                    if (esc) { esc = false; continue; }
                    if (ch == '\\') { esc = true; continue; }
                    if (ch == '"') { inStr = !inStr; continue; }
                    if (inStr) continue;
                    if (ch == '{' || ch == '[') depth++;
                    else if (ch == '}' || ch == ']') depth--;
                }

                // depth 1 = inside the top-level { }, not deeper
                if (depth <= 1) break;
                searchFrom = idx + pattern.Length;
            }

            int colonIdx = json.IndexOf(':', idx + pattern.Length);
            if (colonIdx < 0) return null;

            int valStart = colonIdx + 1;
            while (valStart < json.Length && char.IsWhiteSpace(json[valStart])) valStart++;
            if (valStart >= json.Length) return null;

            if (char.IsDigit(json[valStart]) || json[valStart] == '-')
            {
                int numEnd = valStart;
                while (numEnd < json.Length && (char.IsDigit(json[numEnd]) || json[numEnd] == '.' || json[numEnd] == '-'))
                    numEnd++;
                return json.Substring(valStart, numEnd - valStart);
            }

            if (json[valStart] == 'n') return null;
            if (json[valStart] != '"') return null;

            var sb = new StringBuilder();
            int i2 = valStart + 1;
            while (i2 < json.Length)
            {
                char c = json[i2];
                if (c == '\\' && i2 + 1 < json.Length)
                {
                    char next = json[i2 + 1];
                    switch (next)
                    {
                        case '"': sb.Append('"'); i2 += 2; continue;
                        case '\\': sb.Append('\\'); i2 += 2; continue;
                        case '/': sb.Append('/'); i2 += 2; continue;
                        case 'n': sb.Append('\n'); i2 += 2; continue;
                        case 'r': sb.Append('\r'); i2 += 2; continue;
                        case 't': sb.Append('\t'); i2 += 2; continue;
                        case 'u':
                            if (i2 + 5 < json.Length)
                            {
                                string hex = json.Substring(i2 + 2, 4);
                                int codePoint;
                                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out codePoint))
                                {
                                    sb.Append((char)codePoint);
                                    i2 += 6;
                                    continue;
                                }
                            }
                            sb.Append('u'); i2 += 2; continue;
                        default: sb.Append(next); i2 += 2; continue;
                    }
                }
                if (c == '"') break;
                sb.Append(c);
                i2++;
            }
            return sb.ToString();
        }

        public static string ExtractNestedString(string json, string outerKey, string innerKey)
        {
            string inner = ExtractNestedObject(json, outerKey);
            if (inner == null) return ExtractJsonString(json, innerKey);
            return ExtractJsonString(inner, innerKey);
        }

        public static string ExtractToolArgumentsObject(string json)
        {
            string paramsObj = ExtractNestedObject(json, "params");
            string argsObj = ExtractNestedObject(paramsObj, "arguments");
            if (!string.IsNullOrWhiteSpace(argsObj)) return argsObj;
            if (!string.IsNullOrWhiteSpace(paramsObj)) return paramsObj;
            return json;
        }

        public static string ExtractArgString(string json, params string[] keys)
        {
            if (string.IsNullOrWhiteSpace(json) || keys == null) return null;
            foreach (string key in keys)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                string value = ExtractJsonString(json, key);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return null;
        }

        public static string ExtractNestedObject(string json, string key)
        {
            if (json == null) return null;
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return null;

            int colonIdx = json.IndexOf(':', idx + pattern.Length);
            if (colonIdx < 0) return null;

            int braceStart = json.IndexOf('{', colonIdx);
            if (braceStart < 0) return null;

            int depth = 0;
            bool inString = false;
            bool escape = false;
            for (int i = braceStart; i < json.Length; i++)
            {
                char c = json[i];
                if (escape) { escape = false; continue; }
                if (c == '\\') { escape = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '{') depth++;
                if (c == '}') { depth--; if (depth == 0) return json.Substring(braceStart, i - braceStart + 1); }
            }
            return null;
        }

        public static string[] ExtractJsonStringArray(string json, string key)
        {
            if (json == null) return null;
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return null;

            int colonIdx = json.IndexOf(':', idx + pattern.Length);
            if (colonIdx < 0) return null;

            int bracketStart = json.IndexOf('[', colonIdx);
            if (bracketStart < 0) return null;

            int depth = 0;
            bool inString = false;
            bool escape = false;
            int bracketEnd = -1;
            for (int i = bracketStart; i < json.Length; i++)
            {
                char c = json[i];
                if (escape) { escape = false; continue; }
                if (c == '\\') { escape = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '[') depth++;
                if (c == ']') { depth--; if (depth == 0) { bracketEnd = i; break; } }
            }
            if (bracketEnd < 0) return null;

            string arrayContent = json.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);
            var result = new List<string>();

            int pos = 0;
            while (pos < arrayContent.Length)
            {
                int quoteStart = arrayContent.IndexOf('"', pos);
                if (quoteStart < 0) break;

                var val = new StringBuilder();
                int j = quoteStart + 1;
                while (j < arrayContent.Length)
                {
                    char c = arrayContent[j];
                    if (c == '\\' && j + 1 < arrayContent.Length)
                    {
                        char next = arrayContent[j + 1];
                        switch (next)
                        {
                            case '"': val.Append('"'); break;
                            case '\\': val.Append('\\'); break;
                            case 'n': val.Append('\n'); break;
                            case 'r': val.Append('\r'); break;
                            case 't': val.Append('\t'); break;
                            case 'u':
                                if (j + 5 < arrayContent.Length)
                                {
                                    string hex = arrayContent.Substring(j + 2, 4);
                                    int cp;
                                    if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                                        System.Globalization.CultureInfo.InvariantCulture, out cp))
                                    {
                                        val.Append((char)cp);
                                        j += 6;
                                        continue;
                                    }
                                }
                                val.Append('u'); break;
                            default: val.Append(next); break;
                        }
                        j += 2;
                        continue;
                    }
                    if (c == '"') break;
                    val.Append(c);
                    j++;
                }
                result.Add(val.ToString());
                pos = j + 1;
            }

            return result.Count > 0 ? result.ToArray() : null;
        }

        public static string EscapeJsonString(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        // ── JSON-RPC response builders ──

        public static string BuildResult(string idToken, string resultJson)
        {
            if (idToken == null) return null;
            return @"{ ""jsonrpc"": ""2.0"", ""id"": " + idToken + @", ""result"": " + resultJson + " }";
        }

        public static string BuildError(string idToken, int code, string message)
        {
            string msg = EscapeJsonString(message);
            string err = @"{ ""code"": " + code + @", ""message"": """ + msg + @""" }";
            if (idToken == null) return null;
            return @"{ ""jsonrpc"": ""2.0"", ""id"": " + idToken + @", ""error"": " + err + " }";
        }

        public static string BuildToolResult(string idToken, string text, bool isError = false)
        {
            string escaped = EscapeJsonString(text);
            string content = @"{ ""type"": ""text"", ""text"": """ + escaped + @""" }";
            string result = @"{ ""content"": [" + content + @"], ""isError"": " + (isError ? "true" : "false") + " }";
            return BuildResult(idToken, result);
        }
    }
}
