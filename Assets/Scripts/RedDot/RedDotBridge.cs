using System;
using System.Collections.Generic;
using RedDot.Events;
using UnityEngine;
using XLua;

namespace RedDot
{
    /// <summary>The state of one red dot node: whether it shows, and what number it shows.</summary>
    public readonly struct RedDotState : IEquatable<RedDotState>
    {
        public static readonly RedDotState Hidden = new RedDotState(false, 0);

        public RedDotState(bool visible, int count)
        {
            Visible = visible;
            Count = count;
        }

        public bool Visible { get; }

        public int Count { get; }

        public bool Equals(RedDotState other) => Visible == other.Visible && Count == other.Count;

        public override bool Equals(object obj) => obj is RedDotState other && Equals(other);

        public override int GetHashCode() => (Visible ? 1 : 0) ^ (Count << 1);

        public override string ToString() => Visible ? (Count > 0 ? "* " + Count : "*") : "-";
    }

    /// <summary>Counters the Lua engine keeps, exposed so tests can assert how much work happened.</summary>
    public readonly struct RedDotStats
    {
        public RedDotStats(int flushes, int leafEvaluations, int aggregations, int notifications, int dispatches)
        {
            Flushes = flushes;
            LeafEvaluations = leafEvaluations;
            Aggregations = aggregations;
            Notifications = notifications;
            Dispatches = dispatches;
        }

        /// <summary>Flushes that found something to do. An idle flush is not counted.</summary>
        public int Flushes { get; }

        /// <summary>How often a rule's <c>evaluate</c> ran.</summary>
        public int LeafEvaluations { get; }

        /// <summary>How often a parent recomputed its aggregate.</summary>
        public int Aggregations { get; }

        /// <summary>How many change notifications were delivered.</summary>
        public int Notifications { get; }

        /// <summary>How many events reached Lua.</summary>
        public int Dispatches { get; }

        public override string ToString()
        {
            return $"flushes={Flushes} leafEvaluations={LeafEvaluations} aggregations={Aggregations} " +
                   $"notifications={Notifications} dispatches={Dispatches}";
        }
    }

    /// <summary>
    /// A view that wants to be told when a red dot changes. Implemented by the FairyGUI
    /// adapter, and by test doubles.
    /// </summary>
    /// <remarks>
    /// Implement <see cref="SetRedDot"/> implicitly, as a public method. Lua reaches a
    /// handle by member name through xLua reflection, and an explicit interface
    /// implementation is a private method under a mangled name -- so it compiles, binds,
    /// and then silently never fires.
    /// </remarks>
    public interface IRedDotHandle
    {
        void SetRedDot(string path, bool visible, int count);
    }

    /// <summary>Everything <see cref="RedDotBridge"/> needs to boot.</summary>
    public sealed class RedDotBridgeOptions
    {
        /// <summary>Ordered Lua search roots. The first entry wins, so patches go first.</summary>
        public List<string> ScriptRoots { get; } = new List<string>();

        public EventBus Bus { get; set; }

        public RedDotContext Context { get; set; }

        public ISeenPersistence SeenPersistence { get; set; }

        /// <summary>Where Lua-side warnings go. Defaults to <see cref="Debug.LogWarning(object)"/>.</summary>
        public Action<string> Log { get; set; }

        /// <summary>The shipped script location: <c>Assets/Lua</c>.</summary>
        public static string DefaultScriptRoot => Application.dataPath.Replace('\\', '/') + "/Lua";
    }

    /// <summary>
    /// The single seam between C# and the Lua red dot engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// C# knows about four things and no more: how to boot Lua, how to raise an event,
    /// how to flush, and how to hand a view to a path. It knows nothing about mail,
    /// quests, shops, counts or badge modes — all of that lives in
    /// <c>Assets/Lua/reddot/rules.lua</c> and can be replaced at runtime.
    /// </para>
    /// <para>
    /// Only strings, booleans and numbers cross the boundary in either direction. No
    /// Lua table is ever marshalled into C#, and no C# delegate is ever handed to Lua,
    /// which keeps the bridge free of generated glue code and cheap to reason about.
    /// </para>
    /// </remarks>
    public sealed class RedDotBridge : IDisposable
    {
        private const string BootstrapChunkName = "reddot_bootstrap";

        private readonly LuaEnv _env;
        private readonly LuaScriptLoader _loader = new LuaScriptLoader();
        private readonly EventBus _bus;
        private readonly RedDotContext _context;
        private readonly Action<string> _log;

        /// <summary>Bus tokens, one per event Lua asked to be forwarded.</summary>
        private readonly Dictionary<string, int> _busTokens = new Dictionary<string, int>(StringComparer.Ordinal);

        private readonly List<LuaFunction> _functions = new List<LuaFunction>();

        private readonly LuaFunction _fnDispatch;
        private readonly LuaFunction _fnFlush;
        private readonly LuaFunction _fnMarkSeen;
        private readonly LuaFunction _fnGetState;
        private readonly LuaFunction _fnReloadRules;
        private readonly LuaFunction _fnReloadRulesFromModule;
        private readonly LuaFunction _fnDumpStates;
        private readonly LuaFunction _fnDebugDump;
        private readonly LuaFunction _fnSubscribedEvents;
        private readonly LuaFunction _fnStats;
        private readonly LuaFunction _fnResetStats;
        private readonly LuaFunction _fnBind;
        private readonly LuaFunction _fnUnbind;
        private readonly LuaFunction _fnUnbindAll;
        private readonly LuaFunction _fnBindingCount;

        private bool _disposed;

        /// <summary>Raised for every node whose state actually changed during a flush.</summary>
        public event Action<string, RedDotState> Changed;

        public EventBus Bus => _bus;

        public RedDotContext Context => _context;

        public LuaScriptLoader Loader => _loader;

        public LuaEnv Env => _env;

        public RedDotBridge(RedDotBridgeOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _bus = options.Bus ?? new EventBus();
            _context = options.Context ?? throw new ArgumentException(
                "RedDotBridge needs a context: it is the entire surface of game data the rules may read.",
                nameof(options));
            _log = options.Log ?? (message => Debug.LogWarning(message));

            if (options.ScriptRoots.Count == 0)
            {
                options.ScriptRoots.Add(RedDotBridgeOptions.DefaultScriptRoot);
            }

            foreach (var root in options.ScriptRoots)
            {
                _loader.AddRoot(root);
            }

            _env = new LuaEnv();
            _env.AddLoader(_loader.Load);

            var host = new LuaHost(this);
            _env.Global.Set("CS_HOST", host);
            _env.Global.Set("CS_CTX", _context);
            _env.Global.Set("CS_SEEN", options.SeenPersistence);

            _env.DoString(Bootstrap, BootstrapChunkName);

            _fnDispatch = Resolve("RedDot.dispatch");
            _fnFlush = Resolve("RedDot.flush");
            _fnMarkSeen = Resolve("RedDot.markSeen");
            _fnGetState = Resolve("RedDot.getState");
            _fnReloadRules = Resolve("RedDot.reloadRules");
            _fnReloadRulesFromModule = Resolve("RedDot.reloadRulesFromModule");
            _fnDumpStates = Resolve("RedDot.dumpStates");
            _fnDebugDump = Resolve("RedDot.debugDump");
            _fnSubscribedEvents = Resolve("RedDot.subscribedEvents");
            _fnStats = Resolve("RedDot.stats");
            _fnResetStats = Resolve("RedDot.resetStats");
            _fnBind = Resolve("RedDot.bind");
            _fnUnbind = Resolve("RedDot.unbind");
            _fnUnbindAll = Resolve("RedDot.unbindAll");
            _fnBindingCount = Resolve("RedDot.bindingCount");
        }

        #region Public API

        /// <summary>
        /// Publishes a game event. Events the rules never name as triggers cost a
        /// dictionary miss and nothing else — they never reach Lua.
        /// </summary>
        public void RaiseEvent(string name, string payload = null)
        {
            ThrowIfDisposed();
            _bus.Publish(name, payload);
        }

        /// <summary>
        /// Evaluates everything the events since the last flush made dirty, exactly once
        /// per node, and notifies the nodes that changed. Call it once per frame.
        /// </summary>
        /// <returns>How many nodes changed. Zero means nothing was dirty.</returns>
        public int Flush()
        {
            ThrowIfDisposed();
            return AsInt(_fnFlush.Call());
        }

        /// <summary>
        /// Records that the player has looked at <paramref name="path"/>. Passing an
        /// interior node marks its whole subtree, which is what opening a tab means.
        /// Persistent badges ignore this.
        /// </summary>
        /// <returns>How many nodes flipped to seen. They are dirty until the next flush.</returns>
        public int MarkSeen(string path)
        {
            ThrowIfDisposed();
            return AsInt(_fnMarkSeen.Call(path));
        }

        public RedDotState GetState(string path)
        {
            ThrowIfDisposed();
            var results = _fnGetState.Call(path);
            if (results == null || results.Length < 2)
            {
                return RedDotState.Hidden;
            }

            return new RedDotState(Convert.ToBoolean(results[0]), Convert.ToInt32(results[1]));
        }

        public bool IsVisible(string path) => GetState(path).Visible;

        /// <summary>
        /// Swaps in a new rule table from Lua source, diffing the event subscriptions and
        /// re-evaluating the whole tree. Existing bindings survive, because they hold
        /// paths rather than nodes.
        /// </summary>
        /// <param name="luaSource">
        /// A chunk that returns either a rule table or <c>{ nodes = ..., rules = ... }</c>.
        /// </param>
        /// <returns>How many nodes changed as a result.</returns>
        public int ReloadRules(string luaSource)
        {
            ThrowIfDisposed();
            if (luaSource == null)
            {
                throw new ArgumentNullException(nameof(luaSource));
            }

            return AsInt(_fnReloadRules.Call(luaSource));
        }

        /// <summary>
        /// Re-requires a Lua module and reloads the rules from it. Combined with a patch
        /// root registered through <see cref="AddPatchRoot"/>, this is how a downloaded
        /// file replaces the shipped rules with no C# change at all.
        /// </summary>
        public int ReloadRulesFromModule(string moduleName = "reddot.rules")
        {
            ThrowIfDisposed();
            return AsInt(_fnReloadRulesFromModule.Call(moduleName));
        }

        /// <summary>
        /// Registers a script root that shadows the shipped one. Modules already loaded
        /// keep their old body until something re-requires them.
        /// </summary>
        public void AddPatchRoot(string absolutePath)
        {
            ThrowIfDisposed();
            _loader.AddRoot(absolutePath, asPatch: true);
        }

        /// <summary>
        /// Binds a view to a path. The handle is pushed the current state immediately,
        /// so a view that binds late is correct on its first frame.
        /// </summary>
        /// <param name="owner">
        /// Optional grouping key so a screen can release everything it bound in one
        /// <see cref="UnbindAll"/> call. Defaults to the handle itself.
        /// </param>
        public void Bind(string path, IRedDotHandle handle, string owner = null)
        {
            ThrowIfDisposed();
            if (handle == null)
            {
                throw new ArgumentNullException(nameof(handle));
            }

            _fnBind.Call(path, handle, owner);
        }

        /// <summary>Removes one binding. Unbinding something that is not bound is a no-op.</summary>
        public bool Unbind(string path, IRedDotHandle handle)
        {
            ThrowIfDisposed();
            var results = _fnUnbind.Call(path, handle);
            return results != null && results.Length > 0 && Convert.ToBoolean(results[0]);
        }

        /// <summary>Releases every binding registered under <paramref name="owner"/>.</summary>
        public int UnbindAll(string owner)
        {
            ThrowIfDisposed();
            return AsInt(_fnUnbindAll.Call(owner));
        }

        /// <summary>Bindings on one path, or on everything when <paramref name="path"/> is null.</summary>
        public int BindingCount(string path = null)
        {
            ThrowIfDisposed();
            return AsInt(_fnBindingCount.Call(path));
        }

        /// <summary>
        /// The whole tree as <c>path=visible:count</c> pairs, sorted and semicolon
        /// separated. One call, no table marshalling.
        /// </summary>
        public string DumpStates()
        {
            ThrowIfDisposed();
            return AsString(_fnDumpStates.Call());
        }

        /// <summary>Parsed form of <see cref="DumpStates"/>.</summary>
        public Dictionary<string, RedDotState> ReadAllStates()
        {
            var states = new Dictionary<string, RedDotState>(StringComparer.Ordinal);
            var dump = DumpStates();
            if (string.IsNullOrEmpty(dump))
            {
                return states;
            }

            foreach (var entry in dump.Split(';'))
            {
                if (entry.Length == 0)
                {
                    continue;
                }

                var equals = entry.LastIndexOf('=');
                var colon = entry.LastIndexOf(':');
                if (equals < 0 || colon < equals)
                {
                    continue;
                }

                var path = entry.Substring(0, equals);
                var visible = entry[equals + 1] == '1';
                var count = int.Parse(entry.Substring(colon + 1));
                states[path] = new RedDotState(visible, count);
            }

            return states;
        }

        /// <summary>An indented, human-readable rendering of the tree, the seen set and the stats.</summary>
        public string DebugDump()
        {
            ThrowIfDisposed();
            return AsString(_fnDebugDump.Call());
        }

        /// <summary>Events the current rule table asked to be subscribed to, sorted.</summary>
        public IReadOnlyList<string> SubscribedEvents()
        {
            ThrowIfDisposed();
            var joined = AsString(_fnSubscribedEvents.Call());
            if (string.IsNullOrEmpty(joined))
            {
                return Array.Empty<string>();
            }

            return joined.Split(';');
        }

        public RedDotStats Stats()
        {
            ThrowIfDisposed();
            var results = _fnStats.Call();
            if (results == null || results.Length < 5)
            {
                return default;
            }

            return new RedDotStats(
                Convert.ToInt32(results[0]),
                Convert.ToInt32(results[1]),
                Convert.ToInt32(results[2]),
                Convert.ToInt32(results[3]),
                Convert.ToInt32(results[4]));
        }

        public void ResetStats()
        {
            ThrowIfDisposed();
            _fnResetStats.Call();
        }

        #endregion

        #region Lua-facing host

        /// <summary>
        /// The object Lua sees as <c>CS_HOST</c>. It is the manager's event bus adapter
        /// and its logger at once, and the way change notifications get back to C#.
        /// </summary>
        public sealed class LuaHost
        {
            private readonly RedDotBridge _bridge;

            internal LuaHost(RedDotBridge bridge)
            {
                _bridge = bridge;
            }

            /// <summary>Lua asks for an event to be forwarded. Called on boot and on every reload.</summary>
            public void Subscribe(string eventName)
            {
                _bridge.SubscribeToBus(eventName);
            }

            /// <summary>Lua no longer has a rule triggered by this event.</summary>
            public void Unsubscribe(string eventName)
            {
                _bridge.UnsubscribeFromBus(eventName);
            }

            public void Log(string message)
            {
                _bridge._log(message);
            }

            public void OnChanged(string path, bool visible, int count)
            {
                _bridge.Changed?.Invoke(path, new RedDotState(visible, count));
            }
        }

        private void SubscribeToBus(string eventName)
        {
            if (string.IsNullOrEmpty(eventName) || _busTokens.ContainsKey(eventName))
            {
                return;
            }

            _busTokens[eventName] = _bus.Subscribe(eventName, ForwardToLua);
        }

        private void UnsubscribeFromBus(string eventName)
        {
            if (string.IsNullOrEmpty(eventName) || !_busTokens.TryGetValue(eventName, out var token))
            {
                return;
            }

            _bus.Unsubscribe(token);
            _busTokens.Remove(eventName);
        }

        private void ForwardToLua(string eventName, string payload)
        {
            if (_disposed)
            {
                return;
            }

            _fnDispatch.Call(eventName, payload);
        }

        #endregion

        #region Plumbing

        private LuaFunction Resolve(string path)
        {
            var function = _env.Global.GetInPath<LuaFunction>(path);
            if (function == null)
            {
                throw new InvalidOperationException(
                    "The red dot bootstrap did not define " + path + ". Are the Lua roots correct? Roots: " +
                    string.Join(", ", _loader.Roots));
            }

            _functions.Add(function);
            return function;
        }

        private static int AsInt(object[] results)
        {
            if (results == null || results.Length == 0 || results[0] == null)
            {
                return 0;
            }

            return Convert.ToInt32(results[0]);
        }

        private static string AsString(object[] results)
        {
            if (results == null || results.Length == 0)
            {
                return string.Empty;
            }

            return results[0] as string ?? string.Empty;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RedDotBridge));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (var token in _busTokens.Values)
            {
                _bus.Unsubscribe(token);
            }

            _busTokens.Clear();

            // Cached functions hold Lua registry references; they have to go before the
            // environment they live in.
            foreach (var function in _functions)
            {
                function.Dispose();
            }

            _functions.Clear();

            _env.DoString("collectgarbage('collect')", "reddot_teardown");
            _env.Dispose();
        }

        #endregion

        /// <summary>
        /// The one piece of Lua that lives in C#: it wires the modules to the host
        /// objects and exposes a flat function surface, so that everything the bridge
        /// calls is a plain <c>RedDot.something</c> with string and number arguments.
        /// </summary>
        private const string Bootstrap = @"
local manager_mod = require('reddot.manager')
local binder_mod  = require('reddot.binder')
local types       = require('reddot.types')
local rules       = require('reddot.rules')

local host = CS_HOST
local function log(message) host:Log(message) end

local RedDot = {}

RedDot.types   = types
RedDot.manager = manager_mod.new({
    nodes       = types.nodes,
    rules       = rules,
    bus         = host,
    ctx         = CS_CTX,
    seenBackend = CS_SEEN,
    log         = log,
})
RedDot.binder = binder_mod.new(RedDot.manager, log)

RedDot.manager:addListener(function(path, state)
    host:OnChanged(path, state.visible, state.count)
end)

function RedDot.dispatch(name, payload) return RedDot.manager:dispatch(name, payload) end
function RedDot.flush()                 return RedDot.manager:flush() end
function RedDot.markSeen(path)          return RedDot.manager:markSeen(path) end
function RedDot.getState(path)          return RedDot.manager:getState(path) end
function RedDot.dumpStates()            return RedDot.manager:dumpStates() end
function RedDot.debugDump()             return RedDot.manager:debugDump() end
function RedDot.resetStats()            RedDot.manager:resetStats() end

function RedDot.subscribedEvents()
    return table.concat(RedDot.manager:subscribedEvents(), ';')
end

function RedDot.stats()
    local s = RedDot.manager.stats
    return s.flushes, s.leafEvaluations, s.aggregations, s.notifications, s.dispatches
end

function RedDot.bind(path, handle, owner) return RedDot.binder:bind(path, handle, owner) end
function RedDot.unbind(path, handle)      return RedDot.binder:unbind(path, handle) end
function RedDot.unbindAll(owner)          return RedDot.binder:unbindAll(owner) end
function RedDot.bindingCount(path)        return RedDot.binder:bindingCount(path) end

-- Compiles a patch chunk and hands whatever it returns to the manager. Lua 5.1
-- and 5.3 disagree about the name of the compiler, hence the lookup.
function RedDot.reloadRules(source, chunkName)
    local compile = loadstring or load
    local chunk, err = compile(source, chunkName or 'reddot_patch')
    if not chunk then
        error('reddot: patch failed to compile: ' .. tostring(err), 0)
    end
    return RedDot.manager:reloadRules(chunk())
end

-- Drops the module from the cache first, so the loader gets another chance to
-- resolve it -- which is how a patch root shadows the shipped file.
function RedDot.reloadRulesFromModule(moduleName)
    moduleName = moduleName or 'reddot.rules'
    package.loaded[moduleName] = nil
    return RedDot.manager:reloadRules(require(moduleName))
end

_G.RedDot = RedDot
";
    }
}
