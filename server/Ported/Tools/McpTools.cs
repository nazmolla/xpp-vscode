using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace XppVsCode.Server.Tools
{
    /// <summary>How the server treats an agent tool.</summary>
    [Flags]
    internal enum ToolTraits
    {
        None = 0,
        /// <summary>Read-only: runs in parallel with other requests (calls that need the metadata API still take turns on the bridge's lock).</summary>
        ParallelRead = 1,
        /// <summary>Changes one element (objectType/objectName): the activity log keeps the element before and after.</summary>
        WritesElement = 2,
        /// <summary>Served but not listed in tools/list (an alias of a listed tool).</summary>
        Unlisted = 4,
    }

    /// <summary>One agent tool: its name, traits, handler, and the definition agents discover in tools/list.</summary>
    internal sealed class McpTool
    {
        public McpTool(string name, ToolTraits traits, Func<string, string, string> handler)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A tool needs a name.", nameof(name));
            Name = name;
            Traits = traits;
            Handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public string Name { get; }
        public ToolTraits Traits { get; }
        /// <summary>Takes the JSON-RPC id token and the whole tools/call request; returns the JSON-RPC response.</summary>
        public Func<string, string, string> Handler { get; }
        /// <summary>name, description and inputSchema from Resources/Tools.json; null for unlisted tools.</summary>
        public Dictionary<string, object> Definition { get; internal set; }
        public bool Is(ToolTraits trait) => (Traits & trait) == trait;
    }

    /// <summary>The agent tools the server serves. tools/list is generated from it, with the definitions in Resources/Tools.json.</summary>
    internal sealed class McpToolRegistry
    {
        internal const string DefinitionsResource = "XppVsCode.Server.Tools.json";
        private readonly Dictionary<string, McpTool> _tools = new Dictionary<string, McpTool>(StringComparer.Ordinal);
        private readonly List<McpTool> _listed = new List<McpTool>();
        private string _listJson;

        public void Add(string name, ToolTraits traits, Func<string, string, string> handler)
        {
            if (_listJson != null) throw new InvalidOperationException("Tools are registered before the definitions are attached.");
            if (_tools.ContainsKey(name ?? ""))
                throw new InvalidOperationException("Tool '" + name + "' is registered twice.");
            var tool = new McpTool(name, traits, handler);
            _tools.Add(name, tool);
        }

        /// <summary>The tool, or null when there is none of that name.</summary>
        public McpTool Find(string name) => name != null && _tools.TryGetValue(name, out var tool) ? tool : null;

        public IReadOnlyList<McpTool> All => _tools.Values.ToList();

        /// <summary>Tools in the order of tools/list.</summary>
        public IReadOnlyList<McpTool> Listed => _listed;

        /// <summary>Attaches the definitions (in their order) to the registered tools. Every definition needs a registered tool and
        /// every listed tool a definition, so what agents discover and what the server serves can't drift apart.</summary>
        public void AttachDefinitions(string definitionsJson)
        {
            var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(definitionsJson);
            var definitions = ((System.Collections.ArrayList)root["tools"]).Cast<Dictionary<string, object>>().ToList();
            var problems = new List<string>();
            foreach (var definition in definitions)
            {
                string name = definition.TryGetValue("name", out object n) ? n as string : null;
                McpTool tool = Find(name);
                if (tool == null) problems.Add("defined but not registered: " + name);
                else if (tool.Is(ToolTraits.Unlisted)) problems.Add("defined but registered as unlisted: " + name);
                else if (tool.Definition != null) problems.Add("defined twice: " + name);
                else { tool.Definition = definition; _listed.Add(tool); }
            }
            foreach (var tool in _tools.Values.Where(t => !t.Is(ToolTraits.Unlisted) && t.Definition == null))
                problems.Add("registered but not defined: " + tool.Name);
            if (problems.Count > 0)
                throw new InvalidOperationException("Tool definitions don't match the registered tools: " + string.Join("; ", problems));
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            _listJson = "{\"tools\":[" + string.Join(",", _listed.Select(t => serializer.Serialize(t.Definition))) + "]}";
        }

        /// <summary>The result of tools/list.</summary>
        public string ListJson() => _listJson ?? throw new InvalidOperationException("Tool definitions are not attached.");

        public static string LoadDefinitions()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(DefinitionsResource))
            {
                if (stream == null) throw new InvalidOperationException("Resource " + DefinitionsResource + " is missing.");
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }
    }
}
