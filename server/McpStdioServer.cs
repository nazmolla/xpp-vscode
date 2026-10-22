using System;
using System.IO;
using System.Reflection;
using System.Text;
using XppVsCode.Server.Tools;

namespace XppVsCode.Server
{
    /// <summary>MCP over stdio: one JSON-RPC message per line, as VS Code's MCP client sends them.</summary>
    internal sealed class McpStdioServer
    {
        private const string ServerName = "xpp-vscode";
        private readonly ToolRouter _router;
        private readonly BridgeClient _bridge;
        private readonly string _version;
        private readonly string _instructions;
        private CodeIntel _codeIntel;

        public McpStdioServer(ToolRouter router, BridgeClient bridge, string version)
        {
            _router = router;
            _bridge = bridge;
            _version = version;
            _instructions = LoadInstructions();
        }

        public void Run()
        {
            var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

            string line;
            while ((line = input.ReadLine()) != null)
            {
                if (line.Length == 0) continue;

                string idToken = null;
                string response;
                try
                {
                    idToken = JsonHelpers.ExtractJsonValueToken(line, "id");
                    response = Handle(line, idToken);
                }
                catch (Exception ex)
                {
                    Log.Write("Request failed: " + ex);
                    response = idToken != null ? JsonHelpers.BuildError(idToken, -32603, "Internal error: " + ex.Message) : null;
                }

                // Responses must be single-line JSON.
                if (response != null) output.WriteLine(response.Replace("\r", "").Replace("\n", ""));
            }
        }

        private string Handle(string json, string idToken)
        {
            string method = JsonHelpers.ExtractJsonString(json, "method");
            if (idToken == null) return null;

            switch (method)
            {
                case "initialize":
                    return JsonHelpers.BuildResult(idToken, BuildInitializeResult(
                        JsonHelpers.ExtractNestedString(json, "params", "protocolVersion")));
                case "ping":
                    return JsonHelpers.BuildResult(idToken, "{}");
                case "tools/list":
                    return _router.BuildToolsListResponse(idToken);
                case "tools/call":
                    return _router.HandleToolCall(idToken, json);
                case "xpp/bridge":
                    // Used by the VS Code extension itself (designer, code view): raw bridge JSON, not tool text.
                    string paramsJson = JsonHelpers.ExtractNestedObject(json, "params") ?? "{}";
                    string action = JsonHelpers.ExtractJsonString(paramsJson, "action");
                    string body = JsonHelpers.ExtractNestedObject(paramsJson, "body") ?? "{}";
                    if (action == "where_used")
                        return JsonHelpers.BuildResult(idToken, _router.WhereUsedJson(body));
                    if (action == "list_tags")
                        return JsonHelpers.BuildResult(idToken, _router.TagsJson(body));
                    if (action == "tag_references")
                        return JsonHelpers.BuildResult(idToken, _router.TagReferencesJson(body));
                    return JsonHelpers.BuildResult(idToken, _bridge.Call(action, body));
                case "xpp/lang":
                    // Language features of the VS Code extension (its own server process): raw JSON results.
                    string langParams = JsonHelpers.ExtractNestedObject(json, "params") ?? "{}";
                    _codeIntel = _codeIntel ?? new CodeIntel(_router.XRef, _bridge);
                    return JsonHelpers.BuildResult(idToken, _codeIntel.Handle(JsonHelpers.ExtractJsonString(langParams, "action"),
                        JsonHelpers.ExtractNestedObject(langParams, "body") ?? "{}"));
                default:
                    return JsonHelpers.BuildError(idToken, -32601, "Method not found: " + method);
            }
        }

        private string BuildInitializeResult(string requestedProtocol)
        {
            var sb = new StringBuilder("{");
            sb.Append("\"protocolVersion\":\"").Append(JsonHelpers.EscapeJsonString(requestedProtocol ?? "2025-06-18")).Append("\",");
            sb.Append("\"capabilities\":{\"tools\":{}},");
            sb.Append("\"serverInfo\":{\"name\":\"").Append(ServerName).Append("\",\"version\":\"").Append(_version).Append("\"}");
            if (!string.IsNullOrEmpty(_instructions))
                sb.Append(",\"instructions\":\"").Append(JsonHelpers.EscapeJsonString(_instructions)).Append("\"");
            return sb.Append("}").ToString();
        }

        private static string LoadInstructions()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("XppVsCode.Server.SystemPrompt.txt"))
            {
                if (stream == null) return null;
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }
    }

    internal static class Log
    {
        // stdout is reserved for protocol messages.
        public static void Write(string message) => Console.Error.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + message);
    }
}
