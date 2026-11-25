using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Web.Script.Serialization;
using System.Reflection;
using XppVsCode.Server.Analysis;
using XppVsCode.Server.Host;
using XppVsCode.Server.Host.Reports;
using XppVsCode.Server.MetaModel;
using XppVsCode.Server.Tools;
using Xunit;

namespace XppVsCode.Server.Tests
{
    public class JsonEscapingTests
    {
        [Theory]
        [InlineData("plain")]
        [InlineData("quote \" backslash \\ slash /")]
        [InlineData("nul \u0000 bell \u0007 escape \u001b")]
        [InlineData("lines \r\n tab \t separators \u2028 \u2029")]
        public void EscapedStringsParseBackUnchanged(string text)
        {
            string json = "{\"v\":\"" + JsonHelpers.EscapeJsonString(text) + "\"}";
            var parsed = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(json);
            Assert.Equal(text, parsed["v"]);
        }

        [Fact]
        public void NoRawControlCharactersRemain()
        {
            string escaped = JsonHelpers.EscapeJsonString("a\u0000b\u001fc");
            Assert.DoesNotContain(escaped, c => c < ' ');
        }
    }

    public class LearnUrlTests
    {
        [Theory]
        [InlineData("https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/")]
        [InlineData("https://docs.microsoft.com/en-us/dynamics365/")]
        [InlineData("HTTPS://LEARN.MICROSOFT.COM/x")]
        public void LearnUrlsAreAllowed(string url) => Assert.True(DocSearchHandler.IsLearnUrl(url));

        [Theory]
        [InlineData("https://learn.microsoft.com.invalid/en-us/x")]
        [InlineData("https://learn.microsoft.com@127.0.0.1:9/x")]
        [InlineData("https://learn.microsoft.com:8443/x")]
        [InlineData("http://learn.microsoft.com/x")]
        [InlineData("file:///C:/Windows/win.ini")]
        [InlineData("https://evil.example/?u=https://learn.microsoft.com")]
        [InlineData("learn.microsoft.com/x")]
        [InlineData("")]
        [InlineData(null)]
        public void OtherUrlsAreRefused(string url) => Assert.False(DocSearchHandler.IsLearnUrl(url));
    }

    public class LabelNameTests
    {
        [Theory]
        [InlineData("SYS", true)]
        [InlineData("My_Labels1", true)]
        [InlineData("..\\..\\x", false)]
        [InlineData("a/b", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void LabelFileIds(string id, bool valid) => Assert.Equal(valid, LabelStore.IsLabelFileId(id));

        [Theory]
        [InlineData("en-US", true)]
        [InlineData("zh-Hans", true)]
        [InlineData("de", true)]
        [InlineData("..\\x", false)]
        [InlineData("en-US\\..", false)]
        public void Languages(string language, bool valid) => Assert.Equal(valid, LabelStore.IsLanguage(language));

        [Theory]
        [InlineData(null, "@SYS7149", "SYS", "@SYS7149")]
        [InlineData(null, "@SCM:SalesStatusLabel", "SCM", "SalesStatusLabel")]
        [InlineData("Other", "@SCM:SalesStatusLabel", "SCM", "SalesStatusLabel")]
        [InlineData("SYS", "SYS7149", "SYS", "SYS7149")]
        [InlineData(null, "SYS7149", null, "SYS7149")]
        public void LabelReferencesNameTheirFile(string fileIn, string idIn, string file, string id)
        {
            LabelService.SplitLabelReference(ref fileIn, ref idIn);
            Assert.Equal(file, fileIn);
            Assert.Equal(id, idIn);
        }
    }

    public class TypedFormMetadataTests
    {
        [Fact]
        public void PolymorphicFormControlsAreAppliedFromTypedMetadata()
        {
            Type axFormType = Type.GetType(
                "Microsoft.Dynamics.AX.Metadata.MetaModel.AxForm, Microsoft.Dynamics.AX.Metadata",
                throwOnError: true);
            Type axFormDesignType = Type.GetType(
                "Microsoft.Dynamics.AX.Metadata.MetaModel.AxFormDesign, Microsoft.Dynamics.AX.Metadata",
                throwOnError: true);
            var form = Activator.CreateInstance(axFormType);

            string json = @"{
              ""Name"":""ExampleReviewIssue"",
              ""DataSources"":[{""Name"":""ExampleReviewIssue"",""Table"":""ExampleReviewIssue"",""AllowCreate"":""No""}],
              ""Design"":{""Name"":""Design"",""Pattern"":""SimpleList"",""Controls"":[
                {""Name"":""ReviewActionPane"",""Type"":""ActionPane""},
                {""Name"":""ReviewGrid"",""Type"":""Grid"",""DataSource"":""ExampleReviewIssue"",""Controls"":[
                  {""Name"":""RunId"",""Type"":""String"",""DataSource"":""ExampleReviewIssue"",""DataField"":""RunId""},
                  {""Name"":""Severity"",""Type"":""ComboBox"",""DataSource"":""ExampleReviewIssue"",""DataField"":""Severity""}
                ]}
              ]}
            }";

            System.Reflection.MethodInfo apply = typeof(MetaModelBridge).GetMethod(
                "ApplyTypedMetadataGraph",
                BindingFlags.NonPublic | BindingFlags.Static);

            Exception exception = null;
            try
            {
                apply.Invoke(null, new object[] { form, "AxForm", json });
            }
            catch (TargetInvocationException ex)
            {
                exception = ex.InnerException ?? ex;
            }
            catch (Exception ex)
            {
                exception = ex;
            }

            Assert.Null(exception);
            Assert.Equal("ExampleReviewIssue", axFormType.GetProperty("Name").GetValue(form));

            object design = axFormType.GetProperty("Design").GetValue(form);
            var controls = (IEnumerable)axFormDesignType.GetProperty("Controls").GetValue(design);
            object grid = controls.Cast<object>().FirstOrDefault(c =>
                Convert.ToString(c.GetType().GetProperty("Name")?.GetValue(c)) == "ReviewGrid");
            Assert.NotNull(grid);
            Assert.Equal("AxFormGridControl", grid.GetType().Name);
            Assert.Equal("ExampleReviewIssue", grid.GetType().GetProperty("DataSource")?.GetValue(grid));

            var runId = (IEnumerable)grid.GetType().GetProperty("Controls")?.GetValue(grid);
            object runIdControl = runId?.Cast<object>().FirstOrDefault(c =>
                Convert.ToString(c.GetType().GetProperty("Name")?.GetValue(c)) == "RunId");
            Assert.NotNull(runIdControl);
            Assert.Equal("AxFormStringControl", runIdControl.GetType().Name);
            Assert.Equal("RunId", runIdControl.GetType().GetProperty("DataField")?.GetValue(runIdControl));
        }

        [Fact]
        public void FormControlsNamedByClassAreApplied()
        {
            // "$type" as xpp_read_object writes it and as the New Table with Form wizard sends it.
            Type axFormType = Type.GetType("Microsoft.Dynamics.AX.Metadata.MetaModel.AxForm, Microsoft.Dynamics.AX.Metadata", throwOnError: true);
            var form = Activator.CreateInstance(axFormType);
            string json = @"{""Design"":{""Controls"":[
                {""$type"":""AxFormActionPaneControl"",""Name"":""ActionPane""},
                {""$type"":""AxFormGroupControl"",""Name"":""Filter"",""Controls"":[{""$type"":""AxFormControl"",""Name"":""QuickFilter""}]},
                {""$type"":""AxFormGridControl"",""Name"":""Grid"",""DataSource"":""T"",""Controls"":[{""$type"":""AxFormStringControl"",""Name"":""Grid_Id"",""DataSource"":""T"",""DataField"":""Id""}]}
            ]}}";
            System.Reflection.MethodInfo apply = typeof(MetaModelBridge).GetMethod("ApplyTypedMetadataGraph", BindingFlags.NonPublic | BindingFlags.Static);
            try { apply.Invoke(null, new object[] { form, "AxForm", json }); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }

            object design = axFormType.GetProperty("Design").GetValue(form);
            var controls = ((IEnumerable)design.GetType().GetProperty("Controls").GetValue(design)).Cast<object>().ToList();
            Assert.Equal(new[] { "AxFormActionPaneControl", "AxFormGroupControl", "AxFormGridControl" }, controls.Select(c => c.GetType().Name));
            object field = ((IEnumerable)controls[2].GetType().GetProperty("Controls").GetValue(controls[2])).Cast<object>().Single();
            Assert.Equal("AxFormStringControl", field.GetType().Name);
            Assert.Equal("Id", field.GetType().GetProperty("DataField").GetValue(field));
        }
    }

    public class WritePolicyTests
    {
        private sealed class Policy : IWritePolicy
        {
            public readonly List<string> Calls = new List<string>();
            public string EditDenial(string objectType, string objectName) { Calls.Add("edit " + objectName); return objectName == "Std" ? "standard" : null; }
            public string TargetModelDenial(string modelName) { Calls.Add("model " + modelName); return modelName == "Foundation" ? "not custom" : null; }
        }

        // Services that fail every call, so a test sees whether the dispatcher got past the policy.
        private sealed class FailingService : RealProxy
        {
            private FailingService(Type role) : base(role) { }
            public static T Of<T>() => (T)new FailingService(typeof(T)).GetTransparentProxy();
            public override IMessage Invoke(IMessage msg) =>
                new ReturnMessage(new InvalidOperationException("bridge reached"), (IMethodCallMessage)msg);
        }

        private static (MetaModelBridgeServer server, Policy policy) Create()
        {
            var policy = new Policy();
            var services = new BridgeServices
            {
                Objects = FailingService.Of<IObjectStore>(),
                Discovery = FailingService.Of<IMetadataDiscovery>(),
                Designer = FailingService.Of<IElementDesigner>(),
                Projects = FailingService.Of<IProjectService>(),
                Builds = FailingService.Of<IBuildService>(),
                Models = FailingService.Of<IModelService>(),
                Labels = FailingService.Of<ILabelService>(),
                Packages = FailingService.Of<IProjectPackageService>(),
                Relations = FailingService.Of<ITableRelations>(),
                DataShape = FailingService.Of<ITableDataShape>(),
                Performance = FailingService.Of<IPerformanceAnalysis>(),
                Compatibility = FailingService.Of<IExtensionCompatibility>(),
                Reports = FailingService.Of<IReportDesigns>(),
                Policy = policy,
            };
            return (new MetaModelBridgeServer(services), policy);
        }

        [Theory]
        [InlineData("update_object")]
        [InlineData("delete_object")]
        [InlineData("set_element_property")]
        [InlineData("add_element_node")]
        [InlineData("delete_element_node")]
        [InlineData("rename_element_node")]
        [InlineData("duplicate_element_node")]
        [InlineData("move_element_node")]
        [InlineData("drop_on_node")]
        [InlineData("drop_on_property")]
        [InlineData("set_element_method")]
        public void EditActionsOnStandardObjectsAreRefused(string action)
        {
            var (server, _) = Create();
            string result = server.Dispatch(action, "{\"objectType\":\"AxClass\",\"objectName\":\"Std\"}");
            Assert.Contains("\"success\":false", result);
            Assert.Contains("standard", result);
            Assert.DoesNotContain("bridge reached", result);
        }

        [Theory]
        [InlineData("create_object")]
        [InlineData("duplicate_object")]
        public void CreatingInStandardModelsIsRefused(string action)
        {
            var (server, _) = Create();
            string result = server.Dispatch(action, "{\"objectType\":\"AxClass\",\"objectName\":\"X\",\"modelName\":\"Foundation\"}");
            Assert.Contains("not custom", result);
            Assert.DoesNotContain("bridge reached", result);
        }

        [Fact]
        public void AllowedEditsReachTheBridge()
        {
            var (server, policy) = Create();
            string result = server.Dispatch("delete_object", "{\"objectType\":\"AxClass\",\"objectName\":\"Mine\"}");
            Assert.Contains("bridge reached", result);
            Assert.Equal(new[] { "edit Mine" }, policy.Calls);
        }

        [Fact]
        public void ReadsDoNotConsultThePolicy()
        {
            var (server, policy) = Create();
            server.Dispatch("get_model", "{\"name\":\"Foundation\"}");
            Assert.Empty(policy.Calls);
        }

        [Fact]
        public void ReplacingReferencesInStandardObjectsIsRefused()
        {
            var (server, _) = Create();
            string result = server.Dispatch("replace_references", "{\"objectType\":\"AxForm\",\"objectName\":\"Std\",\"oldName\":\"a\",\"newName\":\"b\"}");
            Assert.Contains("standard", result);
            Assert.DoesNotContain("bridge reached", result);
        }
    }

    /// <summary>Characterizes how every action of the extension's contract is dispatched: which actions exist, which are writes
    /// (checked by the write policy) and which follow the agent's scope. The server refactoring must keep all of it.</summary>
    public class BridgeDispatchTests
    {
        private sealed class RecordingPolicy : IWritePolicy
        {
            public readonly List<string> Calls = new List<string>();
            public string EditDenial(string objectType, string objectName) { Calls.Add("edit"); return null; }
            public string TargetModelDenial(string modelName) { Calls.Add("create"); return null; }
        }

        private sealed class RecordingSession : IAgentSession
        {
            public readonly List<string> Calls = new List<string>();
            public string AgentScopeDenial(string objectType, string objectName) { Calls.Add("scope"); return null; }
            public void BeforeAgentWrite(string objectType, string objectName) => Calls.Add("checkpoint");
            public string CheckpointEntriesJson(string id) => "{\"success\":true,\"entries\":[]}";
            public string RevertCheckpointJson(string id) => "{\"success\":true}";
        }

        private sealed class FailingService : RealProxy
        {
            private FailingService(Type role) : base(role) { }
            public static T Of<T>() => (T)new FailingService(typeof(T)).GetTransparentProxy();
            public override IMessage Invoke(IMessage msg) =>
                new ReturnMessage(new InvalidOperationException("bridge reached"), (IMethodCallMessage)msg);
        }

        internal static MetaModelBridgeServer Create(IWritePolicy policy, IAgentSession session = null) => new MetaModelBridgeServer(new BridgeServices
        {
            Objects = FailingService.Of<IObjectStore>(),
            Discovery = FailingService.Of<IMetadataDiscovery>(),
            Designer = FailingService.Of<IElementDesigner>(),
            Projects = FailingService.Of<IProjectService>(),
            Builds = FailingService.Of<IBuildService>(),
            Models = FailingService.Of<IModelService>(),
            Labels = FailingService.Of<ILabelService>(),
            Packages = FailingService.Of<IProjectPackageService>(),
            Relations = FailingService.Of<ITableRelations>(),
            DataShape = FailingService.Of<ITableDataShape>(),
            Performance = FailingService.Of<IPerformanceAnalysis>(),
            Compatibility = FailingService.Of<IExtensionCompatibility>(),
                Reports = FailingService.Of<IReportDesigns>(),
            Policy = policy,
            AgentWrites = session,
            Checkpoints = session,
        });

        /// <summary>The actions the extension calls (src/bridgeContract.ts).</summary>
        internal static List<string> ContractActions()
        {
            var dir = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "src", "bridgeContract.ts"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string text = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "src", "bridgeContract.ts"));
            text = text.Substring(text.IndexOf('['), text.IndexOf("] as const", StringComparison.Ordinal) - text.IndexOf('['));
            return System.Text.RegularExpressions.Regex.Matches(text, "'(\\w+)'").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToList();
        }

        // Served from the cross-reference database instead of the metadata API.
        internal static readonly string[] XRefActions = { "where_used", "list_tags", "tag_references", "label_usage", "element_dependencies" };

        internal static readonly string[] EditActions =
        {
            "update_object", "delete_object", "set_element_property", "add_element_node", "delete_element_node", "rename_element_node",
            "duplicate_element_node", "move_element_node", "drop_on_node", "drop_on_property", "set_element_method", "replace_references",
            "set_report_design", "create_precision_design", "add_report_data_region", "set_report_dataset_query",
        };
        internal static readonly string[] CreateActions = { "create_object", "duplicate_object", "add_reference", "remove_reference" };

        private const string Body = "{\"objectType\":\"AxClass\",\"objectName\":\"Mine\",\"modelName\":\"testcust\",\"name\":\"testcust\",\"id\":\"1\"}";

        private static IEnumerable<string> MetadataActions() => ContractActions().Except(XRefActions);

        [Fact]
        public void TheContractListsEveryAction()
        {
            var actions = ContractActions();
            Assert.Equal(actions.Count, actions.Distinct().Count());
            Assert.True(actions.Count >= 68, "contract has " + actions.Count + " actions");
            Assert.Subset(new HashSet<string>(actions), new HashSet<string>(EditActions.Concat(CreateActions).Concat(XRefActions)));
        }

        [Fact]
        public void EveryContractActionIsDispatched()
        {
            var server = Create(new RecordingPolicy(), new RecordingSession());
            var unknown = MetadataActions().Where(a => server.Dispatch(a, Body).Contains("Unknown action")).ToList();
            Assert.Empty(unknown);
        }

        [Fact]
        public void UnknownActionsAreReported()
        {
            string result = Create(new RecordingPolicy()).Dispatch("no_such_action", Body);
            Assert.Equal("{\"success\":false,\"message\":\"Unknown action: no_such_action\"}", result);
        }

        [Fact]
        public void OnlyWriteActionsConsultThePolicy()
        {
            var edits = new List<string>();
            var creates = new List<string>();
            foreach (string action in MetadataActions())
            {
                var policy = new RecordingPolicy();
                Create(policy).Dispatch(action, Body);
                if (policy.Calls.Contains("edit")) edits.Add(action);
                if (policy.Calls.Contains("create")) creates.Add(action);
            }
            Assert.Equal(EditActions.OrderBy(a => a), edits.OrderBy(a => a));
            Assert.Equal(CreateActions.OrderBy(a => a), creates.OrderBy(a => a));
        }

        [Fact]
        public void AgentWritesFollowTheScopeAndCheckpoint()
        {
            var scoped = new List<string>();
            foreach (string action in MetadataActions())
            {
                var session = new RecordingSession();
                Create(new RecordingPolicy(), session).Dispatch(action, Body);
                if (session.Calls.SequenceEqual(new[] { "scope", "checkpoint" })) scoped.Add(action);
                else Assert.Empty(session.Calls);
            }
            Assert.Equal(EditActions.Concat(new[] { "create_object", "add_reference", "remove_reference" }).OrderBy(a => a), scoped.OrderBy(a => a));
        }

        [Fact]
        public void CheckOnlyDeletesSkipPolicyAndScope()
        {
            var policy = new RecordingPolicy();
            var session = new RecordingSession();
            Create(policy, session).Dispatch("delete_object", "{\"objectType\":\"AxClass\",\"objectName\":\"Std\",\"checkOnly\":\"true\"}");
            Assert.Empty(policy.Calls);
            Assert.Empty(session.Calls);
        }

        [Fact]
        public void AgentScopeDenialStopsTheWrite()
        {
            var session = new DenyingSession();
            string result = Create(new RecordingPolicy(), session).Dispatch("update_object", Body);
            Assert.Contains("outside the scope", result);
            Assert.DoesNotContain("bridge reached", result);
            Assert.False(session.CheckpointTaken);
        }

        private sealed class DenyingSession : IAgentSession
        {
            public bool CheckpointTaken;
            public string AgentScopeDenial(string objectType, string objectName) => "outside the scope";
            public void BeforeAgentWrite(string objectType, string objectName) => CheckpointTaken = true;
            public string CheckpointEntriesJson(string id) => null;
            public string RevertCheckpointJson(string id) => null;
        }

        [Fact]
        public void CheckpointsWithoutASessionAreUnavailable()
        {
            var server = Create(new RecordingPolicy());
            Assert.Equal("{\"success\":false,\"message\":\"Not available in this process.\"}", server.Dispatch("checkpoint_entries", "{\"id\":\"1\"}"));
            Assert.Equal("{\"success\":false,\"message\":\"Not available in this process.\"}", server.Dispatch("revert_checkpoint", "{\"id\":\"1\"}"));
        }

        [Fact]
        public void ServiceFailuresBecomeFailedResults()
        {
            string result = Create(new RecordingPolicy()).Dispatch("get_model", "{\"name\":\"x\"}");
            Assert.StartsWith("{\"success\":false,\"message\":\"", result);
            Assert.Contains("bridge reached", result);
        }
    }

    /// <summary>The action registry: exactly the contract, write kinds as the policy expects, no duplicates.</summary>
    public class BridgeRegistryTests
    {
        private static MetaModelBridgeServer CreateWithXRef()
        {
            var server = BridgeDispatchTests.Create(new WritePolicyStub());
            new ToolRouter(new BridgeClient(server)).AddXRefActions(server.Actions);
            return server;
        }

        private sealed class WritePolicyStub : IWritePolicy
        {
            public string EditDenial(string objectType, string objectName) => null;
            public string TargetModelDenial(string modelName) => null;
        }

        [Fact]
        public void TheRegistryServesExactlyTheContract()
        {
            var names = CreateWithXRef().Actions.All.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal);
            Assert.Equal(BridgeDispatchTests.ContractActions().OrderBy(n => n, StringComparer.Ordinal), names);
        }

        [Fact]
        public void WriteKindsMatchThePolicyLists()
        {
            var all = CreateWithXRef().Actions.All;
            Assert.Equal(BridgeDispatchTests.EditActions.OrderBy(a => a), all.Where(a => a.Write == WriteKind.Edit).Select(a => a.Name).OrderBy(a => a));
            Assert.Equal(new[] { "add_reference", "create_object", "remove_reference" }, all.Where(a => a.Write == WriteKind.Create).Select(a => a.Name).OrderBy(a => a, StringComparer.Ordinal));
            Assert.Equal(new[] { "duplicate_object" }, all.Where(a => a.Write == WriteKind.Copy).Select(a => a.Name));
        }

        [Fact]
        public void OnlyCrossReferenceActionsRunOutsideTheMetadataLock()
        {
            var unlocked = CreateWithXRef().Actions.All.Where(a => !a.UsesMetadata).Select(a => a.Name).OrderBy(a => a);
            Assert.Equal(BridgeDispatchTests.XRefActions.OrderBy(a => a), unlocked);
            Assert.All(CreateWithXRef().Actions.All.Where(a => !a.UsesMetadata), a => Assert.Equal(WriteKind.Read, a.Write));
        }

        [Fact]
        public void DuplicateRegistrationsAreRefused()
        {
            var registry = new BridgeActionRegistry();
            registry.Add("x", b => b);
            var ex = Assert.Throws<InvalidOperationException>(() => registry.Add("x", WriteKind.Edit, b => b));
            Assert.Contains("'x'", ex.Message);
            Assert.Throws<InvalidOperationException>(() => CreateWithXRef().Actions.Add("where_used", b => b));
        }

        [Fact]
        public void UnnamedActionsAreRefused()
        {
            Assert.Throws<ArgumentException>(() => new BridgeAction(" ", WriteKind.Read, b => b));
            Assert.Throws<ArgumentNullException>(() => new BridgeAction("x", WriteKind.Read, null));
        }

        [Fact]
        public void FindIsExactAndNullSafe()
        {
            var actions = CreateWithXRef().Actions;
            Assert.NotNull(actions.Find("read_object"));
            Assert.Null(actions.Find("READ_OBJECT"));
            Assert.Null(actions.Find(null));
        }

        [Fact]
        public void CrossReferenceActionsDoNotWaitForTheMetadataLock()
        {
            var server = BridgeDispatchTests.Create(new WritePolicyStub());
            var entered = new System.Threading.ManualResetEventSlim();
            var release = new System.Threading.ManualResetEventSlim();
            server.Actions.Add("slow_metadata", b => { entered.Set(); release.Wait(5000); return "{}"; });
            server.Actions.Add(new BridgeAction("quick_xref", WriteKind.Read, b => "{\"quick\":true}", usesMetadata: false));
            var slow = System.Threading.Tasks.Task.Run(() => server.Dispatch("slow_metadata", "{}"));
            Assert.True(entered.Wait(5000));
            try
            {
                var quick = System.Threading.Tasks.Task.Run(() => server.Dispatch("quick_xref", "{}"));
                Assert.True(quick.Wait(2000), "an XRef action waited for the metadata lock");
                Assert.Equal("{\"quick\":true}", quick.Result);
            }
            finally
            {
                release.Set();
                slow.Wait(5000);
            }
        }

        [Fact]
        public void XRefFailuresBecomeFailedResults()
        {
            var server = BridgeDispatchTests.Create(new WritePolicyStub());
            server.Actions.Add(new BridgeAction("broken_xref", WriteKind.Read, b => throw new InvalidOperationException("no database"), usesMetadata: false));
            Assert.Equal("{\"success\":false,\"message\":\"no database\"}", server.Dispatch("broken_xref", "{}"));
        }
    }

    /// <summary>Agent tool registry and discovery (tools/list): every listed tool is served and every served tool is listed.</summary>
    public class ToolRegistryTests
    {
        private static ToolRouter CreateRouter() => new ToolRouter(new BridgeClient(BridgeDispatchTests.Create(new AllowAll())));

        private sealed class AllowAll : IWritePolicy
        {
            public string EditDenial(string objectType, string objectName) => null;
            public string TargetModelDenial(string modelName) => null;
        }

        internal static readonly string[] ExpectedTools =
        {
            "xpp_create_object", "xpp_read_object", "xpp_update_object", "xpp_find_object", "xpp_list_objects", "xpp_list_model_objects",
            "xpp_find_references", "xpp_find_examples", "xpp_analyze_security", "xpp_get_guide", "xpp_find_message_source", "xpp_search_code",
            "xpp_get_model_info", "xpp_list_models", "xpp_read_label", "xpp_create_label", "xpp_add_to_project", "xpp_list_project_items",
            "xpp_get_environment", "xpp_search_docs", "xpp_validate_object", "xpp_get_form_layout", "xpp_delete_object", "xpp_remove_from_project",
            "xpp_select_project", "xpp_build", "xpp_package", "xpp_diff_changes", "xpp_check_best_practice", "xpp_find_deprecations",
            "xpp_analyze_trace", "xpp_analyze_performance", "xpp_get_object_type_schema",
            "xpp_get_report_design", "xpp_set_report_design", "xpp_render_report", "xpp_measure_image", "xpp_compare_layout",
        };

        private static Dictionary<string, object> ToolsList(ToolRouter router)
        {
            var response = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(router.BuildToolsListResponse("7"));
            Assert.Equal(7, response["id"]);
            return (Dictionary<string, object>)response["result"];
        }

        [Fact]
        public void ToolsListHasEveryToolInOrderWithItsSchema()
        {
            var tools = ((System.Collections.ArrayList)ToolsList(CreateRouter())["tools"]).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal(ExpectedTools, tools.Select(t => (string)t["name"]));
            foreach (var tool in tools)
            {
                Assert.False(string.IsNullOrWhiteSpace(tool["description"] as string), (string)tool["name"]);
                var schema = (Dictionary<string, object>)tool["inputSchema"];
                Assert.Equal("object", schema["type"]);
                var properties = schema.TryGetValue("properties", out object p) ? (Dictionary<string, object>)p : new Dictionary<string, object>();
                if (schema.TryGetValue("required", out object required))
                    Assert.All(((System.Collections.ArrayList)required).Cast<string>(), r => Assert.True(properties.ContainsKey(r), tool["name"] + " requires unknown " + r));
            }
        }

        [Fact]
        public void EveryListedToolIsServedAndOnlyAliasesAreUnlisted()
        {
            var registry = CreateRouter().Tools;
            Assert.Equal(ExpectedTools, registry.Listed.Select(t => t.Name));
            Assert.Equal(new[] { "xpp_update_current_object" }, registry.All.Where(t => t.Is(ToolTraits.Unlisted)).Select(t => t.Name));
            Assert.All(registry.Listed, t => Assert.NotNull(t.Definition));
        }

        [Fact]
        public void ParallelReadsAreExactlyTheReadOnlyTools()
        {
            var router = CreateRouter();
            var parallel = new[]
            {
                "xpp_search_code", "xpp_find_examples", "xpp_find_references", "xpp_search_docs", "xpp_read_object", "xpp_find_object",
                "xpp_list_objects", "xpp_list_model_objects", "xpp_get_form_layout", "xpp_read_label", "xpp_get_model_info",
                "xpp_list_models", "xpp_get_environment", "xpp_get_object_type_schema", "xpp_list_project_items", "xpp_validate_object", "xpp_analyze_security",
                "xpp_find_message_source", "xpp_get_guide", "xpp_analyze_performance", "xpp_find_deprecations", "xpp_analyze_trace",
                "xpp_get_report_design", "xpp_render_report", "xpp_measure_image", "xpp_compare_layout",
            };
            Assert.Equal(parallel.OrderBy(n => n), router.Tools.All.Where(t => t.Is(ToolTraits.ParallelRead)).Select(t => t.Name).OrderBy(n => n));
            Assert.All(parallel, n => Assert.True(router.IsParallelRead(n)));
            Assert.False(router.IsParallelRead("xpp_create_object"));
            Assert.False(router.IsParallelRead("xpp_no_such_tool"));
            Assert.False(router.IsParallelRead(null));
            Assert.Empty(router.Tools.All.Where(t => t.Is(ToolTraits.ParallelRead) && t.Is(ToolTraits.WritesElement)));
        }

        [Fact]
        public void ElementWritesAreTheObjectLifeCycleTools()
        {
            var writes = CreateRouter().Tools.All.Where(t => t.Is(ToolTraits.WritesElement)).Select(t => t.Name).OrderBy(n => n);
            Assert.Equal(new[] { "xpp_create_object", "xpp_delete_object", "xpp_update_current_object", "xpp_update_object" }, writes);
        }

        [Fact]
        public void UnknownToolsAreReported()
        {
            string response = CreateRouter().HandleToolCall("3", "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"xpp_nope\",\"arguments\":{}}}");
            Assert.Contains("-32602", response);
            Assert.Contains("Unknown tool: xpp_nope", response);
        }

        [Fact]
        public void WriteToolsOnStandardObjectsReachThePolicy()
        {
            var router = new ToolRouter(new BridgeClient(BridgeDispatchTests.Create(new DenyStd())));
            string response = router.HandleToolCall("4", "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"xpp_update_object\",\"arguments\":{\"objectType\":\"AxClass\",\"objectName\":\"Std\"}}}");
            Assert.Contains("standard object refused", response);
            Assert.DoesNotContain("bridge reached", response);
        }

        private sealed class DenyStd : IWritePolicy
        {
            public string EditDenial(string objectType, string objectName) => objectName == "Std" ? "standard object refused" : null;
            public string TargetModelDenial(string modelName) => null;
        }

        [Fact]
        public void DefinitionsAndRegistrationsMustMatch()
        {
            var registry = new McpToolRegistry();
            registry.Add("a", ToolTraits.None, (id, json) => "");
            registry.Add("b", ToolTraits.None, (id, json) => "");
            var ex = Assert.Throws<InvalidOperationException>(() =>
                registry.AttachDefinitions("{\"tools\":[{\"name\":\"a\",\"description\":\"x\",\"inputSchema\":{\"type\":\"object\"}},{\"name\":\"c\"}]}"));
            Assert.Contains("defined but not registered: c", ex.Message);
            Assert.Contains("registered but not defined: b", ex.Message);
        }

        [Fact]
        public void DuplicateToolsAreRefused()
        {
            var registry = new McpToolRegistry();
            registry.Add("a", ToolTraits.None, (id, json) => "");
            Assert.Throws<InvalidOperationException>(() => registry.Add("a", ToolTraits.ParallelRead, (id, json) => ""));
            Assert.Throws<ArgumentException>(() => registry.Add(null, ToolTraits.None, (id, json) => ""));
        }

        [Fact]
        public void DefinitionsResourceIsEmbedded()
        {
            string json = McpToolRegistry.LoadDefinitions();
            Assert.Contains("\"xpp_create_object\"", json);
        }

        [Theory]
        [InlineData("license")]
        [InlineData("access")]
        public void SecurityOpsWithoutAnObjectNameAskForIt(string op)
        {
            var result = new SecurityAnalysis().Handle(op, k => null, k => k == "objects" ? new[] { "AxSecurityRole:X" } : null, k => false);
            Assert.Equal(false, result["success"]);
            Assert.Contains("objectName", (string)result["message"]);
        }
    }

    /// <summary>The element type table holds the same types per operation as the switches it replaced.</summary>
    public class ElementTypeTableTests
    {
        private static IEnumerable<string> With(string operation) => MetaModelBridge.ElementTypesWith(operation);

        private static readonly string[] Updatable =
        {
            "AxClass", "AxTable", "AxForm", "AxEdt", "AxEnum", "AxView", "AxQuery", "AxDataEntityView", "AxMenu", "AxTableExtension", "AxEnumExtension",
            "AxEdtExtension", "AxFormExtension", "AxSecurityPrivilege", "AxSecurityDuty", "AxSecurityRole", "AxSecurityDutyExtension",
            "AxSecurityRoleExtension", "AxDataEntityViewExtension", "AxMenuExtension", "AxCompositeDataEntityView",
        };

        [Fact]
        public void CreatableTypes() => Assert.Equal(new[]
        {
            "AxClass", "AxTable", "AxForm", "AxEdt", "AxEnum", "AxMenuItemDisplay", "AxMenuItemOutput", "AxMenuItemAction", "AxQuery", "AxView",
            "AxDataEntityView", "AxSecurityPrivilege", "AxSecurityDuty", "AxSecurityRole", "AxService", "AxServiceGroup", "AxMenu", "AxTile",
            "AxConfigurationKey", "AxTableExtension", "AxEnumExtension", "AxEdtExtension", "AxFormExtension", "AxSecurityDutyExtension",
            "AxSecurityRoleExtension", "AxDataEntityViewExtension", "AxLabelFile", "AxMenuExtension", "AxCompositeDataEntityView",
            "AxWorkflowCategory", "AxWorkflowApproval", "AxWorkflowTemplate", "AxReport",
        }.OrderBy(n => n, StringComparer.Ordinal), With("create"));

        [Fact]
        public void TypesWithTheirOwnUpdate() => Assert.Equal(Updatable.OrderBy(n => n, StringComparer.Ordinal), With("update"));

        [Fact]
        public void CreateRunsUpdateForAllButMenuExtensions() =>
            Assert.Equal(Updatable.Where(n => n != "AxMenuExtension").OrderBy(n => n, StringComparer.Ordinal), With("updateAfterCreate"));

        [Fact]
        public void TypesWithTheirOwnRead() => Assert.Equal(new[]
        {
            "AxClass", "AxTable", "AxForm", "AxEnum", "AxEdt", "AxView", "AxQuery", "AxDataEntityView", "AxMenu", "AxMenuItemDisplay", "AxMenuItemOutput",
            "AxMenuItemAction", "AxSecurityPrivilege", "AxSecurityDuty", "AxSecurityRole", "AxService", "AxServiceGroup", "AxTile", "AxConfigurationKey",
            "AxTableExtension", "AxEnumExtension", "AxEdtExtension", "AxFormExtension",
        }.OrderBy(n => n, StringComparer.Ordinal), With("read"));

        [Fact]
        public void EveryUpdatableTypeCanBeCreated() => Assert.Subset(new HashSet<string>(With("create")), new HashSet<string>(Updatable));

        [Fact]
        public void UnknownOperationsHaveNoTypes() => Assert.Empty(With("delete"));
    }

    public class ReadOnlySqlTests
    {
        [Theory]
        [InlineData("SELECT TOP 10 * FROM CUSTTABLE")]
        [InlineData("  select ACCOUNTNUM from CUSTTABLE where DATAAREAID = 'usmf';  ")]
        [InlineData("WITH c AS (SELECT * FROM CUSTTABLE) SELECT COUNT(*) FROM c")]
        [InlineData("SELECT 'DELETE FROM CUSTTABLE' AS Text")]
        [InlineData("SELECT [UPDATE] FROM T -- DROP TABLE T")]
        [InlineData("SELECT 1 /* nested /* EXEC xp */ comment */")]
        [InlineData("SELECT N'it''s; fine'")]
        public void SelectsAreAllowed(string sql) => Assert.Null(ReadOnlySql.Refusal(sql));

        [Theory]
        [InlineData("DELETE FROM CUSTTABLE")]
        [InlineData("UPDATE CUSTTABLE SET NAME = 'x'")]
        [InlineData("SELECT * INTO #t FROM CUSTTABLE")]
        [InlineData("SELECT 1; DROP TABLE CUSTTABLE")]
        [InlineData("WITH c AS (SELECT 1 AS x) DELETE FROM CUSTTABLE")]
        [InlineData("EXEC sp_who")]
        [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'x', 'SELECT 1')")]
        [InlineData("SELECT 1 WAITFOR DELAY '00:10'")]
        [InlineData("SELECT 'unclosed")]
        [InlineData("SELECT 1 /* unclosed")]
        [InlineData("")]
        [InlineData(null)]
        public void EverythingElseIsRefused(string sql) => Assert.NotNull(ReadOnlySql.Refusal(sql));
    }

    public class TraceTreeTests
    {
        private static TraceAnalyzer.Node Node(string method, double ms, params TraceAnalyzer.Node[] children)
        {
            var node = new TraceAnalyzer.Node { Method = method, Calls = 1, Inclusive = ms };
            foreach (var child in children) node.Children[child.Method] = child;
            return node;
        }

        [Fact]
        public void DeepCallStacksSerializeFlat()
        {
            var root = Node("Root::run", 1000);
            var node = root;
            for (int i = 0; i < 3000; i++)
            {
                var child = Node("Class" + i + "::call", 1000);
                node.Children[child.Method] = child;
                node = child;
            }
            var session = new TraceSession();
            var nodes = TraceAnalyzer.FlatTree(root, 5, 400, session.IdOf);

            // The serializer's default recursion limit (100) is what failed on deep traces (issue #12).
            string json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(new Dictionary<string, object> { ["nodes"] = nodes });
            var parsed = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(json);
            Assert.Equal(400, ((System.Collections.ArrayList)parsed["nodes"]).Count);
            Assert.Equal(-1, nodes[0]["p"]);
            Assert.All(nodes.Skip(1), n => Assert.Equal((int)n["id"] - 1, n["p"]));
            Assert.Equal(1, nodes[399]["hidden"]);
        }

        [Fact]
        public void HiddenCallsLoadByPageHeaviestFirst()
        {
            var root = Node("Root::run", 100, Node("A::big", 60), Node("B::small", 3, Node("C::leaf", 1)), Node("D::tiny", 2));
            var session = new TraceSession();
            var nodes = TraceAnalyzer.FlatTree(root, 5, 400, session.IdOf);
            Assert.Equal(new[] { "Root::run", "A::big" }, nodes.Select(n => (string)n["m"]));
            Assert.Equal(2, nodes[0]["hidden"]);

            // The report has the heaviest child; pages continue after what the panel has.
            var more = TraceAnalyzer.ChildrenJson(root, 0, 1, session.IdOf, 1, out int remaining);
            Assert.Equal("B::small", Assert.Single(more)["m"]);
            Assert.Equal(1, more[0]["hidden"]);
            Assert.Equal(1, remaining);
            more = TraceAnalyzer.ChildrenJson(root, 0, 2, session.IdOf, 10, out remaining);
            Assert.Equal("D::tiny", Assert.Single(more)["m"]);
            Assert.Equal(0, remaining);

            // A reloaded panel has only the report again: the same calls load again, with the same ids.
            var again = TraceAnalyzer.ChildrenJson(root, 0, 1, session.IdOf, 10, out remaining);
            Assert.Equal(new[] { "B::small", "D::tiny" }, again.Select(n => (string)n["m"]));
            Assert.Equal(new[] { 2, 3 }, again.Select(n => (int)n["id"]));
            Assert.Equal("C::leaf", Assert.Single(TraceAnalyzer.ChildrenJson(session.NodeAt(2), 2, 0, session.IdOf, 10, out _))["m"]);
        }
    }

    public class ProjectReferenceItemTests
    {
        [Fact]
        public void RemovingAReferenceKeepsAssembliesWithDottedNames()
        {
            string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xpp-ref-test-" + Guid.NewGuid().ToString("N") + ".rnrproj");
            System.IO.File.WriteAllText(file, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\r\n  <ItemGroup>\r\n"
                + "    <ProjectReference Include=\"..\\Contoso\\Contoso.csproj\" />\r\n"
                + "    <Reference Include=\"Contoso.Tools, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null\" />\r\n"
                + "  </ItemGroup>\r\n</Project>\r\n");
            try
            {
                var project = XppProject.ForFile(file);
                Assert.True(project.RemoveReference("Contoso"));
                Assert.Equal(new[] { "Contoso.Tools" }, project.GetReferences().Select(r => r.Name));
                Assert.True(project.RemoveReference("Contoso.Tools"));
                Assert.Empty(project.GetReferences());
                Assert.False(project.RemoveReference("Contoso.Tools"));
            }
            finally
            {
                System.IO.File.Delete(file);
            }
        }
    }

    public class ChangedElementsTests
    {
        [Fact]
        public void StatusLinesMapToElementsOfTheModel()
        {
            var r = ChangedElementsReport.ParseStatus("Metadata/MyPkg/MyModel/",
                " M Metadata/MyPkg/MyModel/AxClass/MyClass.xml\n?? Metadata/MyPkg/MyModel/AxTable/MyTable.xml\nR  Metadata/MyPkg/MyModel/AxEnum/Old.xml -> Metadata/MyPkg/MyModel/AxEnum/New.xml\n M Metadata/Other/AxClass/X.xml\n M Metadata/MyPkg/MyModel/Descriptor/MyModel.xml\n");
            Assert.Equal("modified", r["AxClass/MyClass"]);
            Assert.Equal("added", r["AxTable/MyTable"]);
            Assert.Equal("renamed", r["AxEnum/New"]);
            Assert.Equal(3, r.Count);
        }

        [Fact]
        public void NameStatusLinesMapToElements()
        {
            var r = ChangedElementsReport.ParseNameStatus("M/", "D\tM/AxForm/F.xml\nA\tM/AxClass/C.xml\n");
            Assert.Equal("deleted", r["AxForm/F"]);
            Assert.Equal("added", r["AxClass/C"]);
        }
    }

    public class DataCellTests
    {
        [Fact]
        public void RecIdsKeepEveryDigit() => Assert.Equal("5637144576123456789", DataQueryService.Cell(5637144576123456789L));

        [Fact]
        public void NullsAreNull() => Assert.Null(DataQueryService.Cell(DBNull.Value));

        [Fact]
        public void LongTextIsCut() => Assert.True(((string)DataQueryService.Cell(new string('x', 5000))).Length <= 4001);

        [Fact]
        public void NamesAreQuoted() => Assert.Equal("[a]]b]", DataQueryService.Quote("a]b"));
    }

    public class PerformanceRulesTests
    {
        private static readonly XppVsCode.Server.Analysis.TableFacts Cust = new XppVsCode.Server.Analysis.TableFacts { Name = "CustTable", OverridesDelete = true, HasDeleteActions = true };
        private static readonly XppVsCode.Server.Analysis.TableFacts Line = new XppVsCode.Server.Analysis.TableFacts { Name = "SalesLine" };

        private static List<XppVsCode.Server.Analysis.PerformanceFinding> Run(string source) =>
            XppVsCode.Server.Analysis.PerformanceRules.Analyze("m", source, "", t => t == "CustTable" ? Cust : t == "SalesLine" ? Line : null);

        private static bool Has(List<XppVsCode.Server.Analysis.PerformanceFinding> f, string rule, int line) => f.Exists(x => x.Rule == rule && x.Line == line);

        [Fact]
        public void SelectFirstOnlyInsideWhileSelectIsFlagged()
        {
            var f = Run("void m()\n{\n    SalesLine line;\n    CustTable cust;\n    while select line\n    {\n        select firstonly cust where cust.AccountNum == line.CustAccount;\n    }\n}");
            Assert.True(Has(f, "QueryInLoop", 7));
            Assert.True(Has(f, "AllFields", 5));
        }

        [Fact]
        public void FieldListsAndJoinsAreNotFlagged()
        {
            var f = Run("void m()\n{\n    SalesLine line;\n    CustTable cust;\n    while select ItemId from line join cust where cust.AccountNum == line.CustAccount\n    {\n        info(line.ItemId);\n    }\n}");
            Assert.Empty(f);
        }

        [Fact]
        public void InsertInLoopIsFlagged()
        {
            var f = Run("void m()\n{\n    SalesLine line;\n    int i;\n    for (i = 1; i <= 10; i++)\n    {\n        line.insert();\n    }\n}");
            Assert.True(Has(f, "RowOperationInLoop", 7));
        }

        [Fact]
        public void DeleteFromOnTableWithOverridesFallsBack()
        {
            var f = Run("void m()\n{\n    CustTable cust;\n    delete_from cust where cust.Blocked == 1;\n}");
            Assert.True(Has(f, "RowByRowFallback", 4));
        }

        [Fact]
        public void SkippedCausesAreNotReported()
        {
            var f = Run("void m()\n{\n    CustTable cust;\n    cust.skipDataMethods(true);\n    cust.skipDeleteActions(true);\n    delete_from cust where cust.Blocked == 1;\n}");
            Assert.DoesNotContain(f, x => x.Rule == "RowByRowFallback");
        }

        [Fact]
        public void ForUpdateWithoutWriteIsFlagged()
        {
            var f = Run("void m()\n{\n    CustTable cust;\n    select firstonly forupdate cust where cust.AccountNum == 'x';\n    info(cust.Name);\n}");
            Assert.True(Has(f, "ForUpdateWithoutWrite", 4));
        }

        [Fact]
        public void CommentsAndStringsDoNotCount()
        {
            var f = Run("void m()\n{\n    // while select x { select firstonly y; }\n    str s = \"delete_from cust\";\n    ttsbegin;\n    ttscommit;\n}");
            Assert.Empty(f);
        }

        [Fact]
        public void UnbalancedTransactionsAreFlagged()
        {
            var f = Run("void m()\n{\n    ttsbegin;\n    info('x');\n}");
            Assert.True(Has(f, "UnbalancedTransaction", 3));
        }

        [Fact]
        public void WhereClausesWithoutALeadingIndexFieldAreFlagged()
        {
            var indexed = new XppVsCode.Server.Analysis.TableFacts { Name = "SalesLine", Indexes = { new[] { "SalesId", "LineNum" } } };
            List<XppVsCode.Server.Analysis.PerformanceFinding> Check(string where) => XppVsCode.Server.Analysis.PerformanceRules.Analyze("m",
                "void m()\n{\n    SalesLine line;\n    select firstonly line where " + where + ";\n}", "", t => t == "SalesLine" ? indexed : null);
            Assert.True(Has(Check("line.ItemId == 'A'"), "NoMatchingIndex", 4));
            Assert.False(Has(Check("line.SalesId == 'S' && line.ItemId == 'A'"), "NoMatchingIndex", 4));
            Assert.False(Has(Check("line.RecId == 5"), "NoMatchingIndex", 4));
        }
    }

    public class TraceAnalyzerTests
    {
        private static void Enter(Analysis.TraceAnalyzer a, int thread, double ms, string m) { a.Event(thread, ms); a.Enter(thread, ms, m); }
        private static void Exit(Analysis.TraceAnalyzer a, int thread, double ms, string m) { a.Event(thread, ms); a.Exit(thread, ms, m); }

        [Fact]
        public void BuildsInclusiveAndExclusiveTimesPerRequest()
        {
            var a = new Analysis.TraceAnalyzer();
            Enter(a, 1, 0, "A::run");
            Enter(a, 1, 10, "B::calc");
            a.SqlStatement(1, 15, "SELECT 1", 4);
            Exit(a, 1, 40, "B::calc");
            Enter(a, 1, 50, "B::calc");
            Exit(a, 1, 60, "B::calc");
            Exit(a, 1, 100, "A::run");
            a.Finish(100);

            var request = Assert.Single(a.Requests);
            Assert.Equal(100, request.Duration);
            Assert.False(request.Unfinished);
            Assert.Equal(1, request.SqlCount);
            var b = request.Root.Children["B::calc"];
            Assert.Equal(2, b.Calls);
            Assert.Equal(40, b.Inclusive);
            Assert.Equal(60, request.Root.Exclusive);
            Assert.Equal(1, b.SqlCount);
            Assert.Equal("B::calc", Assert.Single(a.Sql["SELECT 1"].Callers).Key);
        }

        [Fact]
        public void RecursionCountsInclusiveTimeOnce()
        {
            var a = new Analysis.TraceAnalyzer();
            Enter(a, 1, 0, "R::parse");
            Enter(a, 1, 10, "R::parse");
            Exit(a, 1, 30, "R::parse");
            Exit(a, 1, 50, "R::parse");
            a.Finish(50);
            Assert.Equal(50, a.Methods["R::parse"].Inclusive);
            Assert.Equal(50, a.Methods["R::parse"].Exclusive);
            Assert.Equal(2, a.Methods["R::parse"].Calls);
        }

        [Fact]
        public void MissingExitsCloseWithTheirCallerAndOrphanExitsAreIgnored()
        {
            var a = new Analysis.TraceAnalyzer();
            Exit(a, 1, 0, "Before::trace");
            Enter(a, 1, 5, "A::run");
            Enter(a, 1, 10, "Thrower::go");
            Exit(a, 1, 20, "A::run");
            a.Finish(20);
            var request = Assert.Single(a.Requests);
            Assert.Equal(15, request.Duration);
            Assert.Equal(10, request.Root.Children["Thrower::go"].Inclusive);
        }

        [Fact]
        public void RunningRequestsAreUnfinishedAndSilencesAreGaps()
        {
            var a = new Analysis.TraceAnalyzer();
            Enter(a, 1, 0, "A::run");
            Enter(a, 2, 100, "Other::work");
            Exit(a, 2, 900, "Other::work");
            Enter(a, 1, 1000, "B::late");
            a.Finish(1200);
            Assert.True(a.Requests.Single(r => r.Thread == 1).Unfinished);
            Assert.Contains(a.Gaps, g => g.Thread == 1 && g.Duration == 1000 && g.Method == "A::run");
            Assert.Contains(a.Gaps, g => g.Thread == 0 && g.Duration == 800);
        }

        [Fact]
        public void HotPathKeepsWhereTimeSplitsOrIsSpent()
        {
            var a = new Analysis.TraceAnalyzer();
            Enter(a, 1, 0, "A::main");
            Enter(a, 1, 0, "B::wrapper");
            Enter(a, 1, 0, "C::work");
            Exit(a, 1, 900, "C::work");
            Exit(a, 1, 900, "B::wrapper");
            Exit(a, 1, 1000, "A::main");
            a.Finish(1000);
            var path = Analysis.TraceAnalyzer.HotPath(a.Requests[0].Root, 10).Select(n => n.Method).ToList();
            Assert.Equal(new[] { "C::work" }, path);
            Assert.Equal("C::work", Analysis.TraceAnalyzer.TopExclusive(a.Requests[0].Root, 1)[0].Key);
        }
    }

    public class XppSignatureTests
    {
        [Fact]
        public void ParsesModifiersTypesDefaultsAndAttributes()
        {
            var s = Analysis.XppSignature.Parse("/// <summary>x</summary>\n[Hookable(false), SysObsolete('Use other', false)]\npublic static container pack(Map _map, str _name = strFmt('%1,%2', 1, 2), int _n = 0)\n{\n    return conNull();\n}");
            Assert.Equal("pack", s.Name);
            Assert.True(s.IsStatic);
            Assert.Equal("container", s.ReturnType);
            Assert.Equal(new[] { "Map", "str", "int" }, s.ParameterTypes);
            Assert.True(s.HasAttribute("Hookable", "false"));
            Assert.True(s.HasAttribute("SysObsolete"));
            Assert.False(s.HasAttribute("Wrappable", "false"));
        }

        [Fact]
        public void ReportsSignatureChangesButNotDefaults()
        {
            var wrapper = Analysis.XppSignature.Parse("public void run(int _a, str _b)\n{\n    next run(_a, _b);\n}");
            Assert.Null(Analysis.XppSignature.Difference(wrapper, Analysis.XppSignature.Parse("public void run(int _a = 1, str _b = '')\n{\n}")));
            Assert.Contains("2 parameter", Analysis.XppSignature.Difference(Analysis.XppSignature.Parse("void run()\n{\n}"), Analysis.XppSignature.Parse("void run(int _a, str _b)\n{\n}")));
            Assert.Contains("returns boolean", Analysis.XppSignature.Difference(wrapper, Analysis.XppSignature.Parse("public boolean run(int _a, str _b)\n{\n}")));
            Assert.Contains("parameter 2", Analysis.XppSignature.Difference(wrapper, Analysis.XppSignature.Parse("public void run(int _a, real _b)\n{\n}")));
            Assert.Contains("static", Analysis.XppSignature.Difference(wrapper, Analysis.XppSignature.Parse("public static void run(int _a, str _b)\n{\n}")));
        }

        [Fact]
        public void FindsExtensionTargetsWrappersAndHandlers()
        {
            var target = Analysis.XppTarget.ParseExtensionOf("// [ExtensionOf(classStr(Commented))]\n[ExtensionOf(formDataSourceStr(SalesTable, SalesLine))]\nfinal class X_Extension\n{\n}");
            Assert.Equal("formDataSourceStr", target.Kind);
            Assert.Equal("SalesTable", target.Element);
            Assert.Equal("SalesLine", target.Member);
            Assert.True(Analysis.XppTarget.IsWrapper("public int active()\n{\n    int ret = next active();\n    return ret;\n}", "active"));
            Assert.False(Analysis.XppTarget.IsWrapper("public void helper()\n{\n    // next helper();\n}", "helper"));
            var handlers = Analysis.XppTarget.ParseHandlers(Analysis.XppSignature.Parse(
                "[PostHandlerFor(classStr(SalesFormLetter), methodStr(SalesFormLetter, run)), DataEventHandler(tableStr(CustTable), DataEventType::Inserted)]\npublic static void h(XppPrePostArgs _args)\n{\n}")).ToList();
            Assert.Equal(2, handlers.Count);
            Assert.Equal("run", handlers[0].Member);
            Assert.Equal("CustTable", handlers[1].Element);
        }
    }

    public class EventManifestTests
    {
        [Fact]
        public void ReadsEventsAndTemplates()
        {
            var manifest = new Tracing.EventManifest();
            manifest.Add(System.Xml.Linq.XDocument.Parse(@"<instrumentationManifest xmlns='http://schemas.microsoft.com/win/2004/08/events'><instrumentation><events>
<provider name='P' guid='{C0D248CE-634D-426B-9E31-5A50A6D83024}'><events><event value='24500' symbol='XppMethodEnter' template='T'/></events>
<templates><template tid='T'><data name='fileName' inType='win:UnicodeString'/><data name='methodName' inType='win:UnicodeString'/><data name='lineNumber' inType='win:UInt32'/></template></templates></provider>
</events></instrumentation></instrumentationManifest>"));
            var def = manifest.Find(new Guid("C0D248CE-634D-426B-9E31-5A50A6D83024"), 24500);
            Assert.Equal("XppMethodEnter", def.Name);
            Assert.Equal(1, def.IndexOf("methodName"));
        }

        [Fact]
        public void DecodesStringsAndNumbersAndStopsAtTheEnd()
        {
            var def = new Tracing.EventDefinition
            {
                Name = "E",
                Fields = new[]
                {
                    new Tracing.EventField { Name = "a", InType = "win:UnicodeString" },
                    new Tracing.EventField { Name = "b", InType = "win:UInt32" },
                    new Tracing.EventField { Name = "c", InType = "win:Double" },
                    new Tracing.EventField { Name = "d", InType = "win:UnicodeString" },
                },
            };
            var bytes = new List<byte>();
            bytes.AddRange(System.Text.Encoding.Unicode.GetBytes("Hi\0"));
            bytes.AddRange(BitConverter.GetBytes(7u));
            bytes.AddRange(BitConverter.GetBytes(1.5));
            bytes.AddRange(System.Text.Encoding.Unicode.GetBytes("no terminator"));
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(bytes.ToArray(), System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                var e = new Tracing.EtwEvent { UserData = handle.AddrOfPinnedObject(), UserDataLength = bytes.Count, Is64Bit = true };
                var values = Tracing.EventManifest.Decode(e, def, 4);
                Assert.Equal("Hi", values[0]);
                Assert.Equal(7u, values[1]);
                Assert.Equal(1.5, values[2]);
                Assert.Null(values[3]);
            }
            finally
            {
                handle.Free();
            }
        }
    }

    public class ReportPreviewTests
    {
        private const string Ns = "http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition";

        private static string Report(string items, string extra = "", string bodyWidth = "190mm") =>
            "<Report xmlns=\"" + Ns + "\"><ReportSections><ReportSection><Body><ReportItems>" + items + "</ReportItems><Height>100mm</Height></Body>"
            + "<Width>" + bodyWidth + "</Width><Page><PageHeader><Height>15mm</Height></PageHeader><PageHeight>279.4mm</PageHeight><PageWidth>215.9mm</PageWidth>"
            + "<LeftMargin>12mm</LeftMargin><RightMargin>12mm</RightMargin><TopMargin>10mm</TopMargin><BottomMargin>10mm</BottomMargin></Page></ReportSection></ReportSections>"
            + extra + "</Report>";

        [Theory]
        [InlineData("1in", 25.4)]
        [InlineData("72pt", 25.4)]
        [InlineData("2.5cm", 25)]
        [InlineData("6pc", 25.4)]
        [InlineData("12.7mm", 12.7)]
        [InlineData("bad", 0)]
        public void SizesConvertToMillimetres(string size, double mm) => Assert.Equal(mm, RdlPreview.ToMm(size), 3);

        [Fact]
        public void SizesKeepTheirUnit() => Assert.Equal("1.0625in", RdlPreview.FromMm(26.9875, "in"));

        [Fact]
        public void LabelsAndHelpersAreRewrittenForThePreview()
        {
            var unsupported = new HashSet<string>();
            Func<string, string> label = id => id == "@SYS1" ? "Say \"hi\"" : null;
            Assert.Equal("=\"Say \"\"hi\"\"\" & \"x\"", RdlPreview.RewriteExpression("=Labels!@SYS1 & \"x\"", label, unsupported));
            Assert.Equal("=\"Say \"\"hi\"\"\"", RdlPreview.RewriteExpression("=\"Labels!@SYS1\"", label, unsupported));
            Assert.Equal("=Code.FOS_ToDisplayStringAmount(\"en-US\", 1, true)",
                RdlPreview.RewriteExpression("=Microsoft.Dynamics.Framework.Reports.BuiltInMethods.ToDisplayStringAmount(\"en-US\", 1, true)", label, unsupported));
            Assert.Equal("=Code.FOS_SplitReportItem(Fields!A.Value, 1)", RdlPreview.RewriteExpression("=ConversionUtilities.SplitReportItem(Fields!A.Value, 1)", label, unsupported));
            Assert.Equal("=Code.FOS_Unsupported(\"DrillThroughProvider.DrillItem\")", RdlPreview.RewriteExpression("=DrillThroughProvider.DrillItem()", label, unsupported));
            Assert.Equal("=Code.FOS_Unsupported(\"DrillThroughProvider.DrillVoucher\", Fields!V.Value)", RdlPreview.RewriteExpression("=DrillThroughProvider.DrillVoucher(Fields!V.Value)", label, unsupported));
            Assert.Equal(new[] { "DrillThroughProvider.DrillItem", "DrillThroughProvider.DrillVoucher" }, unsupported.OrderBy(u => u));
            Assert.Equal("Literal @SYS1", RdlPreview.RewriteExpression("Literal @SYS1", label, unsupported));
        }

        [Fact]
        public void OnlyReportDefinitionsWithoutDtdsAreRead()
        {
            Assert.NotNull(RdlPreview.Parse(Report(""), out _));
            Assert.Null(RdlPreview.Parse("<Report xmlns=\"urn:other\"/>", out string other));
            Assert.Contains("report definition", other);
            Assert.Null(RdlPreview.Parse("<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY x SYSTEM \"file:///C:/Windows/win.ini\">]><Report xmlns=\"" + Ns + "\">&x;</Report>", out string dtd));
            Assert.Contains("not valid XML", dtd);
        }

        [Fact]
        public void ItemsArePlacedOnThePageInMillimetres()
        {
            var doc = RdlPreview.Parse(Report(
                "<Rectangle Name=\"R\"><ReportItems><Textbox Name=\"T\"><CanGrow>true</CanGrow><Top>2mm</Top><Left>3mm</Left><Height>5mm</Height><Width>20mm</Width></Textbox></ReportItems>"
                + "<Top>1in</Top><Left>10mm</Left><Height>20mm</Height><Width>50mm</Width></Rectangle>"), out _);
            var items = RdlPreview.ItemMap(doc);
            var t = items.Single(i => i.Name == "T");
            // Left margin 12 + rectangle 10 + 3; top margin 10 + header 15 + rectangle 25.4 + 2.
            Assert.Equal(25, t.X, 3);
            Assert.Equal(52.4, t.Y, 3);
            Assert.Equal("R", t.Container);
            Assert.True(t.CanGrow);
        }

        [Fact]
        public void ABodyWiderThanThePageIsWarnedAbout()
        {
            Assert.DoesNotContain(RdlPreview.Check(RdlPreview.Parse(Report(""), out _)), w => w.Contains("wider than the page"));
            Assert.Contains(RdlPreview.Check(RdlPreview.Parse(Report("", bodyWidth: "200mm"), out _)), w => w.Contains("wider than the page"));
        }

        [Fact]
        public void PreparationRebuildsDataAndParametersFromMetadata()
        {
            var doc = RdlPreview.Parse(Report(
                "<Textbox Name=\"T\"><Paragraphs><Paragraph><TextRuns><TextRun><Value>=Fields!Amount.Value &amp; Parameters!Extra.Value &amp; Fields!Unknown.Value</Value></TextRun></TextRuns></Paragraph></Paragraphs><Top>0mm</Top><Left>0mm</Left><Height>5mm</Height><Width>20mm</Width></Textbox>",
                "<CodeModules><CodeModule>Microsoft.Dynamics.Framework.Reports, Version=7.0.0.0</CodeModule></CodeModules>"), out _);
            var sets = new List<ReportDataSetInfo> { new ReportDataSetInfo { Name = "DS", Fields = { new KeyValuePair<string, string>("Amount", "System.Double") } } };
            var prepared = RdlPreview.Prepare(doc, sets, new List<ReportParameterInfo>(), id => null, null, 2, null, "en-US", "USMF", null);
            Assert.DoesNotContain("CodeModule", prepared.Rdl);
            Assert.Contains("FOS_PostDataMethodEvaluation", prepared.Rdl);
            Assert.Equal(2, prepared.Data["DS"].Rows.Count);
            Assert.True(prepared.Data["DS"].Columns.Contains("Unknown"));
            Assert.Equal(typeof(double), prepared.Data["DS"].Columns["Amount"].DataType);
            Assert.Equal("USMF", prepared.Parameters["AX_CompanyName"]);
            Assert.True(prepared.Parameters.ContainsKey("Extra"));
        }

        [Fact]
        public void StaticTextComesFromLiteralsLabelsAndTheFirstRow()
        {
            var table = new System.Data.DataTable();
            table.Columns.Add("Name");
            table.Rows.Add("Contoso");
            var data = new Dictionary<string, System.Data.DataTable> { ["DS"] = table };
            Assert.Equal("Contoso", RdlPreview.StaticText("=First(Fields!Name.Value, \"DS\")", data));
            Assert.Equal("PAY TO", RdlPreview.StaticText("=UCase(\"Pay to\")", data));
            Assert.Equal("Total", RdlPreview.StaticText("Total", data));
            Assert.Null(RdlPreview.StaticText("=Sum(Fields!Amount.Value)", data));
        }

        [Fact]
        public void PaperSizesAndCustomSizesParse()
        {
            Assert.Equal(new[] { 279.4, 215.9 }, ImageMeasure.PaperSize("Letter", landscape: true));
            Assert.Equal(new[] { 215.9, 88.9 }, ImageMeasure.PaperSize("8.5x3.5in", landscape: false).Select(v => Math.Round(v, 3)));
            Assert.Throws<ArgumentException>(() => ImageMeasure.PaperSize("huge", landscape: false));
        }

        [Theory]
        // Rendered sample cheque: CompanyName 12pt bold (capitals, no descenders), BankName 12.5pt (descender g).
        [InlineData("Contoso Entertainment", 3.18, 12.7)]
        [InlineData("Woodgrove Bank", 4.19, 12.8)]
        [InlineData("acme", 1.8, 9.8)]
        public void FontSizeIsEstimatedFromInkHeight(string text, double heightMm, double points) =>
            Assert.Equal(points, ReportTools.EstimatedPoints(text, heightMm), 1);
    }
}
