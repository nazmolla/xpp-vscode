using XppVsCode.Server.MetaModel;

namespace XppVsCode.Server.Tools
{
    /// <summary>In-process stand-in for the HTTP bridge client the Visual Studio version used.</summary>
    internal sealed class BridgeClient
    {
        private readonly MetaModelBridgeServer _server;

        public BridgeClient(MetaModelBridgeServer server)
        {
            _server = server;
        }

        public bool IsAvailable() => true;

        public string Call(string action, string bodyJson) => _server.Dispatch(action, bodyJson ?? "{}");
    }
}
