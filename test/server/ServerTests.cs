using System;
using System.Collections.Generic;
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

        // A bridge that fails every call, so a test sees whether the dispatcher got past the policy.
        private sealed class FailingBridge : RealProxy
        {
            public FailingBridge() : base(typeof(IMetaModelBridge)) { }
            public override IMessage Invoke(IMessage msg) =>
                new ReturnMessage(new InvalidOperationException("bridge reached"), (IMethodCallMessage)msg);
        }

        private static (MetaModelBridgeServer server, Policy policy) Create()
        {
            var policy = new Policy();
            return (new MetaModelBridgeServer((IMetaModelBridge)new FailingBridge().GetTransparentProxy(), policy), policy);
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
    }
}
