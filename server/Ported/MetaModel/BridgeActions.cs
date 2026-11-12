using System;
using System.Collections.Generic;
using System.Linq;

namespace XppVsCode.Server.MetaModel
{
    /// <summary>What a bridge action does to metadata; decides the checks the dispatcher runs before it.</summary>
    public enum WriteKind
    {
        /// <summary>Reads only: no checks.</summary>
        Read,
        /// <summary>Changes the element in objectType/objectName: it must belong to a custom model (unless checkOnly), and the
        /// agent's writes follow the user's scope and take a checkpoint.</summary>
        Edit,
        /// <summary>Creates an element in modelName, which must be a custom model; the agent's writes follow the scope and take a checkpoint.</summary>
        Create,
        /// <summary>Copies an element into modelName, which must be a custom model.</summary>
        Copy,
    }

    /// <summary>One action of the bridge: its name in the extension's contract (src/bridgeContract.ts), its kind and its handler.</summary>
    public sealed class BridgeAction
    {
        public BridgeAction(string name, WriteKind write, Func<string, string> handler, bool usesMetadata = true)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A bridge action needs a name.", nameof(name));
            Name = name;
            Write = write;
            Handler = handler ?? throw new ArgumentNullException(nameof(handler));
            UsesMetadata = usesMetadata;
        }

        public string Name { get; }
        public WriteKind Write { get; }
        /// <summary>Takes the request body (JSON) and returns the result (JSON).</summary>
        public Func<string, string> Handler { get; }
        /// <summary>The metadata provider is not thread-safe, so these actions take turns. Actions that only query the
        /// cross-reference database run at once.</summary>
        public bool UsesMetadata { get; }
    }

    /// <summary>The actions the bridge serves, by name.</summary>
    public sealed class BridgeActionRegistry
    {
        private readonly Dictionary<string, BridgeAction> _actions = new Dictionary<string, BridgeAction>(StringComparer.Ordinal);

        public void Add(BridgeAction action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (_actions.ContainsKey(action.Name))
                throw new InvalidOperationException("Bridge action '" + action.Name + "' is registered twice.");
            _actions.Add(action.Name, action);
        }

        public void Add(string name, WriteKind write, Func<string, string> handler) => Add(new BridgeAction(name, write, handler));

        public void Add(string name, Func<string, string> handler) => Add(new BridgeAction(name, WriteKind.Read, handler));

        /// <summary>The action, or null when there is none of that name.</summary>
        public BridgeAction Find(string name) => name != null && _actions.TryGetValue(name, out var action) ? action : null;

        public IReadOnlyList<BridgeAction> All => _actions.Values.OrderBy(a => a.Name, StringComparer.Ordinal).ToList();
    }
}
