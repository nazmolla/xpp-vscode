using System;
using System.Linq;
using System.Net;
using System.Text;

namespace XppVsCode.Server.Tools
{
    /// <summary>
    /// Handles the xpp_search_docs tool — fetches Microsoft Learn pages
    /// and returns the extracted text content.
    /// </summary>
    internal sealed class DocSearchHandler
    {
        public string Handle(string idToken, string json)
        {
            try
            {
                string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
                string url = JsonHelpers.ExtractArgString(argsJson, "url", "link");
                string query = JsonHelpers.ExtractArgString(argsJson, "query", "q", "search");
                string maxLenStr = JsonHelpers.ExtractArgString(argsJson, "maxLength", "maxLen");
                int maxLen = 12000;
                if (!string.IsNullOrEmpty(maxLenStr))
                    int.TryParse(maxLenStr, out maxLen);
                if (maxLen < 1000) maxLen = 1000;
                if (maxLen > 50000) maxLen = 50000;

                if (string.IsNullOrEmpty(url) && string.IsNullOrEmpty(query))
                    return JsonHelpers.BuildToolResult(idToken, "Either 'url' or 'query' is required.", isError: true);

                string fetchUrl;
                if (!string.IsNullOrEmpty(url))
                {
                    if (!url.StartsWith("https://learn.microsoft.com", StringComparison.OrdinalIgnoreCase)
                        && !url.StartsWith("https://docs.microsoft.com", StringComparison.OrdinalIgnoreCase))
                        return JsonHelpers.BuildToolResult(idToken,
                            "Only Microsoft Learn URLs are allowed (https://learn.microsoft.com/...). "
                            + "Use 'query' parameter to search instead.", isError: true);
                    // AX 2012 class docs describe a different code base; D365FO classes must be read from the live metadata.
                    if (url.IndexOf("dynamics-ax-2012", StringComparison.OrdinalIgnoreCase) >= 0
                        || url.IndexOf("dynamicsax-2012", StringComparison.OrdinalIgnoreCase) >= 0)
                        return JsonHelpers.BuildToolResult(idToken,
                            "AX 2012 documentation does not describe D365FO code. To see a class's methods and source, "
                            + "call xpp_read_object; to see how it is used, call xpp_find_references.", isError: true);
                    fetchUrl = url;
                }
                else
                {
                    // Ranked search first; a keyword guess only when the search service gives nothing usable.
                    var hits = SearchLearn(query);
                    foreach (var hit in hits)
                    {
                        string page = FetchAndExtractPage(hit.Url, maxLen);
                        if (page == null || page.Length < 200) continue;
                        var text = new StringBuilder("Source: " + hit.Url + "\n");
                        var others = hits.Where(h => h.Url != hit.Url).Take(5).ToList();
                        if (others.Count > 0)
                            text.Append("Other matches (pass one as url): " + string.Join("; ", others.Select(o => o.Title + " " + o.Url)) + "\n");
                        text.Append("\n").Append(page);
                        string result = text.ToString();
                        if (result.Length > maxLen)
                            result = result.Substring(0, maxLen) + "\n\n[Content truncated at " + maxLen + " characters]";
                        return JsonHelpers.BuildToolResult(idToken, result);
                    }

                    string guessedUrl = GuessDocsUrl(query);
                    if (guessedUrl != null)
                    {
                        string guessContent = FetchAndExtractPage(guessedUrl, maxLen);
                        if (guessContent != null && guessContent.Length > 200)
                        {
                            McpLogger.Log("xpp_search_docs: guessed URL hit: " + guessedUrl);
                            string resultText2 = "Source: " + guessedUrl + "\n\n" + guessContent;
                            if (resultText2.Length > maxLen)
                                resultText2 = resultText2.Substring(0, maxLen) + "\n\n[Content truncated at " + maxLen + " characters]";
                            return JsonHelpers.BuildToolResult(idToken, resultText2);
                        }
                    }
                    fetchUrl = "https://learn.microsoft.com/en-us/search/?terms="
                        + Uri.EscapeDataString(query) + "&scope=Dynamics+365";
                }

                McpLogger.Log("xpp_search_docs: fetching " + fetchUrl);
                string content = FetchAndExtractPage(fetchUrl, maxLen);
                if (content == null || content.Length < 100)
                    return JsonHelpers.BuildToolResult(idToken,
                        "Could not retrieve useful content from: " + fetchUrl
                        + ". Try a more specific query or provide a direct URL.", isError: true);

                string resultText = "Source: " + fetchUrl + "\n\n" + content;
                if (resultText.Length > maxLen)
                    resultText = resultText.Substring(0, maxLen) + "\n\n[Content truncated at " + maxLen + " characters]";

                return JsonHelpers.BuildToolResult(idToken, resultText);
            }
            catch (Exception ex)
            {
                return JsonHelpers.BuildToolResult(idToken, "Error searching docs: " + ex.Message, isError: true);
            }
        }

        private sealed class SearchHit
        {
            public string Title;
            public string Url;
        }

        private static readonly System.Text.RegularExpressions.Regex CodeIntent = new System.Text.RegularExpressions.Regex(
            @"\b(x\+\+|code|class|method|api|programmatic\w*|extension|coc|chain of command|event handler|developer|compile|build|metadata|table|form|entity|query|sysoperation|batch job)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Microsoft Learn's semantic search (the public Learn MCP endpoint) in relevance order; the keyword search API as fallback.
        // Questions about code get developer pages (dev-itpro) first: end-user pages answer how to use a feature, not how to code it.
        private static System.Collections.Generic.List<SearchHit> SearchLearn(string query)
        {
            bool dev = CodeIntent.IsMatch(query ?? "");
            string q = dev && query.IndexOf("developer", StringComparison.OrdinalIgnoreCase) < 0 ? query + " developer" : query;
            var hits = SearchLearnSemantic(q);
            if (hits.Count == 0) hits = SearchLearnKeyword(q);
            return dev
                ? hits.Select((h, i) => new { h, i }).OrderBy(x => x.h.Url.IndexOf("/dev-itpro/", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                    .ThenBy(x => x.i).Select(x => x.h).ToList()
                : hits;
        }

        private static System.Collections.Generic.List<SearchHit> SearchLearnSemantic(string query)
        {
            var hits = new System.Collections.Generic.List<SearchHit>();
            try
            {
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                string request = serializer.Serialize(new System.Collections.Generic.Dictionary<string, object>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 1,
                    ["method"] = "tools/call",
                    ["params"] = new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["name"] = "microsoft_docs_search",
                        ["arguments"] = new System.Collections.Generic.Dictionary<string, object>
                        {
                            ["query"] = query + " Dynamics 365 finance and operations X++",
                        },
                    },
                });
                string response;
                using (var client = new WebClient { Encoding = Encoding.UTF8 })
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "xpp-vscode";
                    client.Headers[HttpRequestHeader.ContentType] = "application/json";
                    client.Headers[HttpRequestHeader.Accept] = "application/json, text/event-stream";
                    response = client.UploadString("https://learn.microsoft.com/api/mcp", request);
                }

                // The endpoint answers as server-sent events: the JSON-RPC message is on a "data:" line.
                string message = response.Split('\n').Select(l => l.Trim())
                    .Where(l => l.StartsWith("data:", StringComparison.Ordinal))
                    .Select(l => l.Substring(5).Trim()).LastOrDefault() ?? response;
                var root = serializer.DeserializeObject(message) as System.Collections.Generic.Dictionary<string, object>;
                var result = root != null && root.TryGetValue("result", out object r) ? r as System.Collections.Generic.Dictionary<string, object> : null;
                var content = result != null && result.TryGetValue("content", out object c) ? c as object[] : null;
                string text = content?.OfType<System.Collections.Generic.Dictionary<string, object>>()
                    .Select(p => p.TryGetValue("text", out object t) ? t as string : null).FirstOrDefault(t => t != null);
                if (text == null) return hits;

                var payload = serializer.DeserializeObject(text) as System.Collections.Generic.Dictionary<string, object>;
                if (payload == null || !payload.TryGetValue("results", out object results) || !(results is object[] items)) return hits;
                var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items.OfType<System.Collections.Generic.Dictionary<string, object>>())
                {
                    string url = item.TryGetValue("contentUrl", out object u) ? u as string : null;
                    if (url == null || url.IndexOf("/dynamics365/", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (url.IndexOf("dynamics-ax-2012", StringComparison.OrdinalIgnoreCase) >= 0 || url.IndexOf("/business-central/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    int anchor = url.IndexOf('#');
                    if (anchor >= 0) url = url.Substring(0, anchor);
                    url = url.Replace("https://learn.microsoft.com/dynamics365/", "https://learn.microsoft.com/en-us/dynamics365/");
                    if (!seen.Add(url)) continue;
                    hits.Add(new SearchHit { Title = item.TryGetValue("title", out object t) ? t as string : url, Url = url });
                }
            }
            catch (Exception ex)
            {
                McpLogger.Log("xpp_search_docs: Learn semantic search failed: " + ex.Message);
            }
            return hits;
        }

        // Microsoft Learn's public keyword search API; developer (dev-itpro) pages first, then other D365 finance and operations pages.
        private static System.Collections.Generic.List<SearchHit> SearchLearnKeyword(string query)
        {
            var hits = new System.Collections.Generic.List<SearchHit>();
            try
            {
                string api = "https://learn.microsoft.com/api/search?search=" + Uri.EscapeDataString(query + " Dynamics 365 finance operations")
                    + "&locale=en-us&$top=25";
                string json;
                using (var client = new WebClient { Encoding = Encoding.UTF8 })
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "xpp-vscode";
                    json = client.DownloadString(api);
                }
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                if (serializer.DeserializeObject(json) is System.Collections.Generic.Dictionary<string, object> root
                    && root.TryGetValue("results", out object results) && results is object[] items)
                {
                    foreach (var item in items.OfType<System.Collections.Generic.Dictionary<string, object>>())
                    {
                        string url = item.TryGetValue("url", out object u) ? u as string : null;
                        if (url == null || url.IndexOf("/dynamics365/", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (url.IndexOf("dynamics-ax-2012", StringComparison.OrdinalIgnoreCase) >= 0 || url.IndexOf("/business-central/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        hits.Add(new SearchHit { Title = item.TryGetValue("title", out object t) ? t as string : url, Url = url });
                    }
                }
            }
            catch (Exception ex)
            {
                McpLogger.Log("xpp_search_docs: search API failed: " + ex.Message);
            }
            return hits.OrderBy(h => h.Url.IndexOf("/fin-ops-core/dev-itpro/", StringComparison.OrdinalIgnoreCase) >= 0 ? 0
                : h.Url.IndexOf("/fin-ops-core/", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 2).ToList();
        }

        private static string GuessDocsUrl(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            string lower = query.ToLowerInvariant();
            string devItPro = "https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/";

            if (lower.Contains("business event"))
                return devItPro + "business-events/business-events-dev-doc";

            if (lower.Contains("chain of command") || lower.Contains("coc"))
                return devItPro + "extensibility/method-wrapping-coc";
            if (lower.Contains("extension method") && !lower.Contains("chain"))
                return devItPro + "extensibility/class-extensions";
            if (lower.Contains("extensib") && lower.Contains("form"))
                return devItPro + "extensibility/customize-model-elements-extensions";
            if (lower.Contains("extensib") && lower.Contains("table"))
                return devItPro + "extensibility/customize-model-elements-extensions";
            if (lower.Contains("extensib") && lower.Contains("enum"))
                return devItPro + "extensibility/add-enum-value";
            if (lower.Contains("extensib") && (lower.Contains("overview") || lower.Contains("intro")))
                return devItPro + "extensibility/extensibility-home-page";
            if (lower.Contains("event handler") || lower.Contains("event subscription"))
                return devItPro + "extensibility/event-handler-result-class";
            if (lower.Contains("sysextension") || lower.Contains("sysplugin") || lower.Contains("factory pattern"))
                return devItPro + "extensibility/register-subclass-factory-methods";

            if (lower.Contains("select statement") || lower.Contains("select syntax"))
                return devItPro + "dev-ref/xpp-data-query";
            if (lower.Contains("validtimestate") || lower.Contains("date-effective") || lower.Contains("date effective"))
                return devItPro + "dev-ref/xpp-data-query#valid-time-state-tables";
            if (lower.Contains("insert_recordset") || lower.Contains("update_recordset") || lower.Contains("set-based"))
                return devItPro + "dev-ref/xpp-data-query";
            if (lower.Contains("exception") || lower.Contains("try catch") || lower.Contains("error handling"))
                return devItPro + "dev-ref/xpp-exceptions";
            if (lower.Contains("class") && lower.Contains("syntax"))
                return devItPro + "dev-ref/xpp-classes-methods";
            if (lower.Contains("attribute") || lower.Contains("metadata attribute"))
                return devItPro + "dev-ref/xpp-attribute-classes";
            if (lower.Contains("data type") || lower.Contains("primitive"))
                return devItPro + "dev-ref/xpp-data-primitive";
            if (lower.Contains("operator") || lower.Contains("expression"))
                return devItPro + "dev-ref/xpp-operators";
            if (lower.Contains("macro") || lower.Contains("preprocessor"))
                return devItPro + "dev-ref/xpp-macros";
            if (lower.Contains("interface") && lower.Contains("x++"))
                return devItPro + "dev-ref/xpp-interfaces";

            if (lower.Contains("query") && (lower.Contains("framework") || lower.Contains("queryrun") || lower.Contains("querybuild")))
                return devItPro + "dev-ref/xpp-data-query";

            if (lower.Contains("sysoperation") || (lower.Contains("batch") && lower.Contains("framework")))
                return devItPro + "sysadmin/batch-processing-overview";
            if (lower.Contains("data entit") || lower.Contains("dataentity"))
                return devItPro + "data-entities/data-entities";
            if (lower.Contains("ssrs") || lower.Contains("report") && lower.Contains("data provider"))
                return devItPro + "analytics/report-programming-guide";
            if (lower.Contains("security") && (lower.Contains("privilege") || lower.Contains("duty") || lower.Contains("role")))
                return devItPro + "sysadmin/role-based-security";
            if (lower.Contains("number sequence"))
                return devItPro + "organization-administration/number-sequence-overview";
            if (lower.Contains("workflow"))
                return devItPro + "organization-administration/workflow-system-architecture";
            if (lower.Contains("dimension") && lower.Contains("financ"))
                return devItPro + "financial/financial-dimension-configuration-integration";
            if (lower.Contains("model") && (lower.Contains("creat") || lower.Contains("package")))
                return devItPro + "dev-tools/models";

            return null;
        }

        private static string FetchAndExtractPage(string url, int maxLen)
        {
            try
            {
                using (var client = new WebClient())
                {
                    client.Encoding = Encoding.UTF8;
                    client.Headers.Add("User-Agent", "XppCopilotCompanion/1.0 (MCP docs tool)");
                    // Learn serves the article as markdown on request: no navigation chrome, code blocks kept.
                    client.Headers.Add("Accept", "text/markdown, text/html;q=0.5");
                    string body = client.DownloadString(url);

                    string extracted;
                    if ((client.ResponseHeaders?["Content-Type"] ?? "").IndexOf("markdown", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        extracted = body;
                        if (extracted.StartsWith("---"))
                        {
                            int end = extracted.IndexOf("\n---", 3, StringComparison.Ordinal);
                            if (end > 0) extracted = extracted.Substring(extracted.IndexOf('\n', end + 1) + 1);
                        }
                        extracted = extracted.Trim();
                    }
                    else
                    {
                        extracted = HtmlExtractor.ExtractMainContent(body);
                        if (string.IsNullOrWhiteSpace(extracted))
                            extracted = HtmlExtractor.StripHtmlTags(body);
                    }

                    if (extracted.Length > maxLen)
                        extracted = extracted.Substring(0, maxLen);

                    return extracted;
                }
            }
            catch (Exception ex)
            {
                McpLogger.Log("FetchAndExtractPage error for " + url + ": " + ex.Message);
                return null;
            }
        }
    }
}
