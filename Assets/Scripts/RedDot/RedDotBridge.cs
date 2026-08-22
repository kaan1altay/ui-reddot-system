using System;
using System.Collections.Generic;
using System.Globalization;
using RedDot.Events;
using UnityEngine;
using XLua;

namespace RedDot
{
    /// <summary>Counters the Lua engine keeps, exposed so tests can assert how much work happened.</summary>
    public readonly struct RedDotStats
    {
        public RedDotStats(int events, int queued, int computes, int notifications, int drains, int mismatches)
        {
            Events = events;
            Queued = queued;
            Computes = computes;
            Notifications = notifications;
            Drains = drains;
            Mismatches = mismatches;
        }

        /// <summary>Events that reached Lua.</summary>
        public int Events { get; }

        /// <summary>Dots put on the pending queue. Re-queuing an already-pending dot is free.</summary>
        public int Queued { get; }

        /// <summary>How often a rule was actually evaluated.</summary>
        public int Computes { get; }

        /// <summary>Values pushed to subscribers.</summary>
        public int Notifications { get; }

        /// <summary>Ticks that found something on the queue. An idle tick is not one.</summary>
        public int Drains { get; }

        /// <summary>Disagreements the reconcile checker has found.</summary>
        public int Mismatches { get; }

        public override string ToString()
        {
            return $"events={Events} queued={Queued} computes={Computes} " +
                   $"notifications={Notifications} drains={Drains} mismatches={Mismatches}";
        }
    }

    /// <summary>
    /// A view that wants to be told when a red dot changes.
    /// </summary>
    /// <remarks>
    /// Implement <see cref="SetRedDot"/> implicitly, as a public method. Lua reaches a
    /// handle by member name through xLua reflection, and an explicit interface
    /// implementation is a private method under a mangled name — so it compiles, binds,
    /// and then silently never fires.
    /// </remarks>
    public interface IRedDotHandle
    {
        /// <param name="registryKey">The dot's identity, e.g. <c>"MailItem|42"</c>.</param>
        /// <param name="value">Whether the dot is on.</param>
        void SetRedDot(string registryKey, bool value);
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
    /// C# knows how to boot Lua, how to raise an event, how to tick a frame, and how to
    /// hand a view a type and some key values. It knows nothing about what any badge
    /// means — that lives in <c>Assets/Lua/reddot/RedDotRules.lua</c> and can be
    /// replaced at runtime.
    /// </para>
    /// <para>
    /// Only strings, booleans and numbers cross the boundary in either direction. No Lua
    /// table is ever marshalled into C# and no C# delegate is ever handed to Lua, which
    /// keeps the bridge free of generated glue and cheap to reason about.
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

        private readonly LuaFunction _fnQueueEvent;
        private readonly LuaFunction _fnTick;
        private readonly LuaFunction _fnSubscribe;
        private readonly LuaFunction _fnUnsubscribe;
        private readonly LuaFunction _fnClearSubscriptions;
        private readonly LuaFunction _fnMarkSeen;
        private readonly LuaFunction _fnIsSeen;
        private readonly LuaFunction _fnGetValue;
        private readonly LuaFunction _fnGetValueByKey;
        private readonly LuaFunction _fnBuildKey;
        private readonly LuaFunction _fnCounts;
        private readonly LuaFunction _fnSubscriberCount;
        private readonly LuaFunction _fnDumpState;
        private readonly LuaFunction _fnDumpValues;
        private readonly LuaFunction _fnStats;
        private readonly LuaFunction _fnResetStats;
        private readonly LuaFunction _fnSetReconcileEnabled;
        private readonly LuaFunction _fnReconcile;
        private readonly LuaFunction _fnReloadRules;
        private readonly LuaFunction _fnValidateSource;
        private readonly LuaFunction _fnCreateGlobalRedDots;
        private readonly LuaFunction _fnNextDeadline;

        private bool _disposed;

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

            _fnQueueEvent = Resolve("RedDot.queueEvent");
            _fnTick = Resolve("RedDot.tick");
            _fnSubscribe = Resolve("RedDot.subscribe");
            _fnUnsubscribe = Resolve("RedDot.unsubscribe");
            _fnClearSubscriptions = Resolve("RedDot.clearSubscriptions");
            _fnMarkSeen = Resolve("RedDot.markSeen");
            _fnIsSeen = Resolve("RedDot.isSeen");
            _fnGetValue = Resolve("RedDot.getValue");
            _fnGetValueByKey = Resolve("RedDot.getValueByKey");
            _fnBuildKey = Resolve("RedDot.buildKey");
            _fnCounts = Resolve("RedDot.counts");
            _fnSubscriberCount = Resolve("RedDot.subscriberCount");
            _fnDumpState = Resolve("RedDot.dumpState");
            _fnDumpValues = Resolve("RedDot.dumpValues");
            _fnStats = Resolve("RedDot.stats");
            _fnResetStats = Resolve("RedDot.resetStats");
            _fnSetReconcileEnabled = Resolve("RedDot.setReconcileEnabled");
            _fnReconcile = Resolve("RedDot.reconcile");
            _fnReloadRules = Resolve("RedDot.reloadRules");
            _fnValidateSource = Resolve("RedDot.validateSource");
            _fnCreateGlobalRedDots = Resolve("RedDot.createGlobalRedDots");
            _fnNextDeadline = Resolve("RedDot.nextDeadline");
        }

        #region Events and the frame tick

        /// <summary>
        /// Publishes a game event. Events no rule names as a trigger cost a dictionary
        /// miss and never reach Lua.
        /// </summary>
        public void RaiseEvent(string name, string payload = null)
        {
            ThrowIfDisposed();
            _bus.Publish(name, payload);
        }

        /// <summary>
        /// One frame's work: fire anything the clock made due, compute every queued dot
        /// exactly once, notify the ones that moved, and persist at most one save.
        /// </summary>
        /// <returns>How many dots changed. Zero means the queue was empty.</returns>
        public int Flush()
        {
            ThrowIfDisposed();
            return AsInt(_fnTick.Call());
        }

        #endregion

        #region Subscription

        /// <summary>
        /// Binds a handle to the dot identified by <paramref name="type"/> and
        /// <paramref name="keys"/>, creating it if this is the first subscriber. The
        /// current value is pushed before this returns.
        /// </summary>
        /// <returns>The registry key, which <see cref="Unsubscribe"/> wants back.</returns>
        public string Subscribe(IRedDotHandle handle, string type, params object[] keys)
        {
            ThrowIfDisposed();
            if (handle == null)
            {
                throw new ArgumentNullException(nameof(handle));
            }

            return AsString(_fnSubscribe.Call(Args(handle, type, keys)));
        }

        public bool Unsubscribe(string registryKey, IRedDotHandle handle)
        {
            ThrowIfDisposed();
            return AsBool(_fnUnsubscribe.Call(registryKey, handle));
        }

        /// <summary>
        /// Drops every subscriber and destroys every keyed dot. What a screen stack calls
        /// at a state-change boundary, so a screen torn down without unbinding cannot keep
        /// dots alive for the rest of the session.
        /// </summary>
        public int ClearSubscriptions()
        {
            ThrowIfDisposed();
            return AsInt(_fnClearSubscriptions.Call());
        }

        public int SubscriberCount(string registryKey)
        {
            ThrowIfDisposed();
            return AsInt(_fnSubscriberCount.Call(registryKey));
        }

        #endregion

        #region Values and seen state

        /// <summary>
        /// Records that the player has looked, storing the token the rule reports now.
        /// Types that do not track seen state ignore it.
        /// </summary>
        public bool MarkSeen(string type, params object[] keys)
        {
            ThrowIfDisposed();
            return AsBool(_fnMarkSeen.Call(Args(type, keys)));
        }

        public bool IsSeen(string type, params object[] keys)
        {
            ThrowIfDisposed();
            return AsBool(_fnIsSeen.Call(Args(type, keys)));
        }

        /// <summary>The cached value, or false when no such dot is live.</summary>
        public bool GetValue(string type, params object[] keys)
        {
            ThrowIfDisposed();
            return AsBool(_fnGetValue.Call(Args(type, keys)));
        }

        public bool GetValueByKey(string registryKey)
        {
            ThrowIfDisposed();
            return AsBool(_fnGetValueByKey.Call(registryKey));
        }

        /// <summary>Builds a registry key without touching the registry.</summary>
        public string BuildKey(string type, params object[] keys)
        {
            ThrowIfDisposed();
            return AsString(_fnBuildKey.Call(Args(type, keys)));
        }

        #endregion

        #region Rules

        /// <summary>
        /// Swaps in a new rule table from Lua source, diffing the event subscriptions,
        /// creating any newly global dots and re-evaluating everything. Existing bindings
        /// survive, because they hold a type and keys rather than an object.
        /// </summary>
        /// <param name="luaSource">
        /// A chunk returning either a rule table or <c>{ rules = ..., events = ... }</c>,
        /// where <c>events</c> declares the event names the patch introduces.
        /// </param>
        /// <returns>How many dots changed as a result.</returns>
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
        /// Runs boot validation over a rule chunk <em>without</em> applying it, and
        /// returns one <c>level: message</c> line per problem.
        /// </summary>
        public IReadOnlyList<string> ValidateSource(string luaSource)
        {
            ThrowIfDisposed();
            var joined = AsString(_fnValidateSource.Call(luaSource));
            return string.IsNullOrEmpty(joined) ? Array.Empty<string>() : joined.Split('\n');
        }

        /// <summary>Creates the keyless dots for any global type that has none yet.</summary>
        public int CreateGlobalRedDots()
        {
            ThrowIfDisposed();
            return AsInt(_fnCreateGlobalRedDots.Call());
        }

        /// <summary>The soonest scheduled reset, in unix seconds, or null when none is set.</summary>
        public long? NextDeadline()
        {
            ThrowIfDisposed();
            var results = _fnNextDeadline.Call();
            if (results == null || results.Length == 0 || results[0] == null)
            {
                return null;
            }

            return Convert.ToInt64(results[0]);
        }

        #endregion

        #region Diagnostics

        /// <summary>Live dots, and how many of them are keyed rather than global.</summary>
        public (int Total, int Keyed) Counts()
        {
            ThrowIfDisposed();
            var results = _fnCounts.Call();
            if (results == null || results.Length < 2)
            {
                return (0, 0);
            }

            return (Convert.ToInt32(results[0]), Convert.ToInt32(results[1]));
        }

        public int GetRedDotCount() => Counts().Total;

        /// <summary>
        /// Turns on a once-a-second sweep that recomputes every live dot and logs the
        /// ones that disagree with the cache. It fixes nothing: a MISMATCH means a rule
        /// is missing an event, and papering over it would hide that.
        /// </summary>
        public void SetReconcileEnabled(bool enabled)
        {
            ThrowIfDisposed();
            _fnSetReconcileEnabled.Call(enabled);
        }

        /// <summary>Sweeps now instead of waiting for the timer. Returns the disagreement count.</summary>
        public int Reconcile()
        {
            ThrowIfDisposed();
            return AsInt(_fnReconcile.Call());
        }

        /// <summary>All live dots as <c>key=0|1:subscribers</c>, sorted, semicolon separated.</summary>
        public string DumpValues()
        {
            ThrowIfDisposed();
            return AsString(_fnDumpValues.Call());
        }

        /// <summary>Parsed form of <see cref="DumpValues"/>.</summary>
        public Dictionary<string, (bool Value, int Subscribers)> ReadAllValues()
        {
            var values = new Dictionary<string, (bool, int)>(StringComparer.Ordinal);
            var dump = DumpValues();
            if (string.IsNullOrEmpty(dump))
            {
                return values;
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

                var key = entry.Substring(0, equals);
                var value = entry[equals + 1] == '1';
                var subscribers = int.Parse(entry.Substring(colon + 1), CultureInfo.InvariantCulture);
                values[key] = (value, subscribers);
            }

            return values;
        }

        /// <summary>An indented, human-readable rendering of the registry, seen set and stats.</summary>
        public string DumpState()
        {
            ThrowIfDisposed();
            return AsString(_fnDumpState.Call());
        }

        /// <summary>Events the current rules asked to be subscribed to, sorted.</summary>
        public IReadOnlyList<string> SubscribedEvents() => _bus.SubscribedEvents();

        public RedDotStats Stats()
        {
            ThrowIfDisposed();
            var results = _fnStats.Call();
            if (results == null || results.Length < 6)
            {
                return default;
            }

            return new RedDotStats(
                Convert.ToInt32(results[0]),
                Convert.ToInt32(results[1]),
                Convert.ToInt32(results[2]),
                Convert.ToInt32(results[3]),
                Convert.ToInt32(results[4]),
                Convert.ToInt32(results[5]));
        }

        public void ResetStats()
        {
            ThrowIfDisposed();
            _fnResetStats.Call();
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

        #endregion

        #region Lua-facing host

        /// <summary>
        /// The object Lua sees as <c>CS_HOST</c>: the manager's event bus adapter and its
        /// logger at once.
        /// </summary>
        public sealed class LuaHost
        {
            private readonly RedDotBridge _bridge;

            internal LuaHost(RedDotBridge bridge)
            {
                _bridge = bridge;
            }

            /// <summary>Lua asks for an event to be forwarded. Called on boot and every reload.</summary>
            public void Subscribe(string eventName)
            {
                _bridge.SubscribeToBus(eventName);
            }

            /// <summary>No rule is triggered by this event any more.</summary>
            public void Unsubscribe(string eventName)
            {
                _bridge.UnsubscribeFromBus(eventName);
            }

            public void Log(string message)
            {
                _bridge._log(message);
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

            _fnQueueEvent.Call(eventName, payload);
        }

        #endregion

        #region Plumbing

        /// <summary>
        /// Builds the argument array for a variadic Lua call. Key values keep their type:
        /// an int stays an int so a rule can hand it straight back to a C# accessor.
        /// </summary>
        private static object[] Args(string type, object[] keys)
        {
            if (keys == null || keys.Length == 0)
            {
                return new object[] { type };
            }

            var args = new object[keys.Length + 1];
            args[0] = type;
            Array.Copy(keys, 0, args, 1, keys.Length);
            return args;
        }

        private static object[] Args(object first, string type, object[] keys)
        {
            var tail = Args(type, keys);
            var args = new object[tail.Length + 1];
            args[0] = first;
            Array.Copy(tail, 0, args, 1, tail.Length);
            return args;
        }

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

        private static bool AsBool(object[] results)
        {
            if (results == null || results.Length == 0 || results[0] == null)
            {
                return false;
            }

            return Convert.ToBoolean(results[0]);
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
        /// objects and exposes a flat function surface, so everything the bridge calls is
        /// a plain <c>RedDot.something</c> taking strings, numbers and booleans.
        /// </summary>
        private const string Bootstrap = @"
local manager_mod = require('reddot.manager')
local RedDotType  = require('reddot.RedDotType')
local RedDotEvent = require('reddot.RedDotEvent')
local rules       = require('reddot.RedDotRules')

-- The one global rules read through. Everything a condition may look at hangs
-- off it, and nothing else is reachable from a rule.
Game = CS_CTX

local host = CS_HOST
local function log(message) host:Log(message) end

local RedDot = {}
RedDot.Type  = RedDotType
RedDot.Event = RedDotEvent

RedDot.manager = manager_mod.new({
    rules       = rules,
    bus         = host,
    clock       = CS_CTX.Clock,
    knownEvents = RedDotEvent,
    seenBackend = CS_SEEN,
    log         = log,
})

-- Global dots exist from boot, so a lobby button is right before its screen has
-- ever been opened.
RedDot.manager:CreateGlobalRedDots()

function RedDot.queueEvent(name, payload)  return RedDot.manager:QueueEvent(name, payload) end
function RedDot.tick()                     return RedDot.manager:Tick() end
function RedDot.clearSubscriptions()       return RedDot.manager:ClearSubscriptions() end
function RedDot.createGlobalRedDots()      return RedDot.manager:CreateGlobalRedDots() end
function RedDot.dumpState()                return RedDot.manager:DumpState() end
function RedDot.dumpValues()               return RedDot.manager:DumpValues() end
function RedDot.resetStats()               RedDot.manager:ResetStats() end
function RedDot.reconcile()                return RedDot.manager:Reconcile() end
function RedDot.nextDeadline()             return RedDot.manager:NextDeadline() end

function RedDot.subscribe(handle, t, ...)  return RedDot.manager:Subscribe(handle, t, ...) end
function RedDot.unsubscribe(key, handle)   return RedDot.manager:Unsubscribe(key, handle) end
function RedDot.subscriberCount(key)       return RedDot.manager:SubscriberCount(key) end

function RedDot.markSeen(t, ...)           return RedDot.manager:MarkSeen(t, ...) end
function RedDot.isSeen(t, ...)             return RedDot.manager:IsSeen(t, ...) end
function RedDot.getValue(t, ...)           return RedDot.manager:GetValue(t, ...) end
function RedDot.getValueByKey(key)         return RedDot.manager:GetValueByKey(key) end

function RedDot.buildKey(t, ...)
    return manager_mod.BuildKey(t, { ... }, select('#', ...))
end

function RedDot.counts()
    return RedDot.manager:GetRedDotCount(), RedDot.manager:GetKeyedCount()
end

function RedDot.setReconcileEnabled(enabled)
    return RedDot.manager:SetReconcileEnabled(enabled)
end

function RedDot.stats()
    local s = RedDot.manager.stats
    return s.events, s.queued, s.computes, s.notifications, s.drains, s.mismatches
end

-- Compiles a patch chunk and hands whatever it returns to the manager. Lua 5.1
-- and 5.3 disagree about the name of the compiler, hence the lookup.
local function compile(source, chunkName)
    local loader = loadstring or load
    local chunk, err = loader(source, chunkName or 'reddot_patch')
    if not chunk then
        error('reddot: patch failed to compile: ' .. tostring(err), 0)
    end
    return chunk
end

function RedDot.reloadRules(source)
    return RedDot.manager:ReloadRules(compile(source)())
end

-- Validates a rule chunk without applying it, so a build step or a test can ask
-- whether a patch is sane before it ever reaches a player.
function RedDot.validateSource(source)
    local spec = compile(source, 'reddot_validate')()
    local ruleTable, extra = spec, nil
    if type(spec) == 'table' and spec.rules ~= nil then
        ruleTable, extra = spec.rules, spec.events
    end

    local known = {}
    for _, name in pairs(RedDotEvent) do known[name] = true end
    for _, name in pairs(manager_mod.entriesOf(extra)) do known[name] = true end

    local lines = {}
    for _, problem in ipairs(manager_mod.ValidateRules(ruleTable, known)) do
        lines[#lines + 1] = problem.level .. ': ' .. problem.message
    end
    return table.concat(lines, '\n')
end

_G.RedDot = RedDot
";
    }
}
