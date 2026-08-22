using System;
using System.Collections.Generic;
using FairyGUI;

namespace RedDot
{
    /// <summary>
    /// Binds FairyGUI components to red dot paths and takes care of the lifetime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RedDotBridge.Bind"/> is the raw seam: it registers a handle and expects
    /// somebody to unregister it. This class is what makes that safe to use from UI code,
    /// where components are disposed by screen teardown and recycled by list pools, in an
    /// order nobody controls.
    /// </para>
    /// <para>
    /// Two guarantees:
    /// </para>
    /// <list type="number">
    /// <item><b>One binding per component.</b> Binding a component that is already bound
    /// releases the previous binding first. That is what makes it safe to reuse a pooled
    /// list item for a different node: the old callback is gone before the new one is
    /// registered, so a recycled row can never light up for the row it used to be.</item>
    /// <item><b>Disposed components release themselves.</b> A component that leaves the
    /// stage while disposed unbinds immediately, and any update aimed at a disposed
    /// component unbinds it on the spot. The second path is the one that matters, because
    /// a component disposed before it was ever on the stage never raises the first.</item>
    /// </list>
    /// </remarks>
    public sealed class RedDotBinder
    {
        private readonly RedDotBridge _bridge;

        private readonly Dictionary<GComponent, Binding> _bindings = new Dictionary<GComponent, Binding>();

        public RedDotBinder(RedDotBridge bridge)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        }

        /// <summary>Bindings this binder currently holds.</summary>
        public int Count => _bindings.Count;

        /// <summary>
        /// Binds the badge inside <paramref name="component"/> to <paramref name="path"/>.
        /// The current state is pushed immediately, so the badge is right on the frame it
        /// appears rather than on the next change.
        /// </summary>
        /// <param name="owner">
        /// Optional grouping key — a screen name, a window, a list — that
        /// <see cref="UnbindAll"/> can release in one call. Without one, the binding lives
        /// until the component is unbound explicitly or disposed.
        /// </param>
        /// <returns>The view driving the badge, so callers can inspect it in tests.</returns>
        public RedDotView Bind(GComponent component, string path, string owner = null)
        {
            if (component == null)
            {
                throw new ArgumentNullException(nameof(component));
            }

            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("A red dot binding needs a node path.", nameof(path));
            }

            if (component.isDisposed)
            {
                throw new ArgumentException("Cannot bind a disposed component.", nameof(component));
            }

            // The pooled-reuse guarantee: whatever this component was showing before, it
            // stops showing it now.
            Unbind(component);

            var binding = new Binding(this, component, path, owner);
            _bindings.Add(component, binding);

            binding.Attach();
            _bridge.Bind(path, binding, owner);

            return binding.View;
        }

        /// <summary>
        /// Releases the binding on <paramref name="component"/>. Unbinding something that
        /// was never bound is a no-op: teardown order in UI code is rarely under anyone's
        /// control.
        /// </summary>
        public bool Unbind(GComponent component)
        {
            if (component == null || !_bindings.TryGetValue(component, out var binding))
            {
                return false;
            }

            Release(binding, unregisterFromLua: true);
            return true;
        }

        /// <summary>
        /// Releases every binding registered under <paramref name="owner"/>. This is the
        /// one call a screen makes when it closes.
        /// </summary>
        public int UnbindAll(string owner)
        {
            if (string.IsNullOrEmpty(owner))
            {
                return 0;
            }

            // Lua owns the owner index, so one call there releases the whole group; this
            // side only has to forget its bookkeeping.
            var released = _bridge.UnbindAll(owner);

            var stale = new List<Binding>();
            foreach (var pair in _bindings)
            {
                if (string.Equals(pair.Value.Owner, owner, StringComparison.Ordinal))
                {
                    stale.Add(pair.Value);
                }
            }

            foreach (var binding in stale)
            {
                Release(binding, unregisterFromLua: false);
            }

            return released;
        }

        /// <summary>
        /// Releases bindings whose component has been disposed. The delivery-time check
        /// normally makes this unnecessary; it is here for a screen that wants to sweep
        /// explicitly rather than wait for the next change.
        /// </summary>
        public int ReapDisposed()
        {
            List<Binding> dead = null;
            foreach (var pair in _bindings)
            {
                if (pair.Key.isDisposed)
                {
                    (dead ??= new List<Binding>()).Add(pair.Value);
                }
            }

            if (dead == null)
            {
                return 0;
            }

            foreach (var binding in dead)
            {
                Release(binding, unregisterFromLua: true);
            }

            return dead.Count;
        }

        /// <summary>The path a component is currently bound to, or null.</summary>
        public string PathOf(GComponent component)
        {
            return component != null && _bindings.TryGetValue(component, out var binding) ? binding.Path : null;
        }

        /// <summary>The view driving a component's badge, or null when it is not bound.</summary>
        public RedDotView ViewOf(GComponent component)
        {
            return component != null && _bindings.TryGetValue(component, out var binding) ? binding.View : null;
        }

        private void Release(Binding binding, bool unregisterFromLua)
        {
            if (!_bindings.Remove(binding.Component))
            {
                return;
            }

            binding.Detach();

            if (unregisterFromLua)
            {
                _bridge.Unbind(binding.Path, binding);
            }
        }

        /// <summary>
        /// The handle the Lua binder actually holds. It sits in front of the view so that
        /// an update aimed at a disposed component releases the binding instead of poking
        /// a dead object.
        /// </summary>
        private sealed class Binding : IRedDotHandle
        {
            private readonly RedDotBinder _binder;
            private readonly EventCallback0 _onRemovedFromStage;

            public Binding(RedDotBinder binder, GComponent component, string path, string owner)
            {
                _binder = binder;
                Component = component;
                Path = path;
                Owner = owner;
                View = new RedDotView(component);

                _onRemovedFromStage = OnRemovedFromStage;
            }

            public GComponent Component { get; }

            public string Path { get; }

            public string Owner { get; }

            public RedDotView View { get; }

            public void Attach()
            {
                Component.onRemovedFromStage.Add(_onRemovedFromStage);
            }

            public void Detach()
            {
                Component.onRemovedFromStage.Remove(_onRemovedFromStage);
            }

            public void SetRedDot(string path, bool visible, int count)
            {
                if (Component.isDisposed)
                {
                    _binder.Release(this, unregisterFromLua: true);
                    return;
                }

                View.SetRedDot(path, visible, count);
            }

            /// <summary>
            /// FairyGUI sets <c>isDisposed</c> before it detaches the object, so this
            /// separates a real teardown from a component that is merely being pooled and
            /// will come back.
            /// </summary>
            private void OnRemovedFromStage()
            {
                if (Component.isDisposed)
                {
                    _binder.Release(this, unregisterFromLua: true);
                }
            }
        }
    }
}
