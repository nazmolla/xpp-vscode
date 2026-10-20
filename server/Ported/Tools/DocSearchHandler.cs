using System;
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

        private static string GuessDocsUrl(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            string lower = query.ToLowerInvariant();
            string devItPro = "https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/";

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
                    client.Headers.Add("Accept", "text/html");
                    string html = client.DownloadString(url);

                    string extracted = HtmlExtractor.ExtractMainContent(html);
                    if (string.IsNullOrWhiteSpace(extracted))
                        extracted = HtmlExtractor.StripHtmlTags(html);

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
