using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace XppVsCode.Server.MetaModel
{
    public sealed partial class MetaModelBridge
    {
        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _publicProperties = new ConcurrentDictionary<Type, PropertyInfo[]>();

        /// <summary>Public instance properties of a metadata type, read once per type (callers must not change the array).</summary>
        internal static PropertyInfo[] PublicProperties(Type type) =>
            _publicProperties.GetOrAdd(type, t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance));
    }
}
