using System;
using System.Reflection;

namespace XppVsCode.Server.MetaModel
{
    public sealed partial class MetaModelBridge
    {
        /// <summary>The message of the exception behind reflection and aggregate wrappers.</summary>
        internal static string Cause(Exception ex)
        {
            while ((ex is TargetInvocationException || ex is AggregateException) && ex.InnerException != null)
                ex = ex.InnerException;
            return ex.Message;
        }

        /// <summary>Logs a failure that the operation tolerates (a cache, an index, optional information).</summary>
        internal static void LogIgnored(string what, Exception ex) => BridgeLogStatic(what + " failed: " + Cause(ex));
    }
}
