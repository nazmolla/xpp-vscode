namespace XppVsCode.Server.MetaModel
{
    // The ported bridge asserted Visual Studio's UI thread; here BridgeDispatcher serializes calls instead.
    internal static class ThreadHelper
    {
        public static void ThrowIfNotOnUIThread() { }
    }
}
