using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Web.Script.Serialization;
using XppVsCode.Server.Host;
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
        };
        internal static readonly string[] CreateActions = { "create_object", "duplicate_object" };

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
            Assert.Equal(EditActions.Concat(new[] { "create_object" }).OrderBy(a => a), scoped.OrderBy(a => a));
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
}
