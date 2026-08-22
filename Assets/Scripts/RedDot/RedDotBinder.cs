using System;
using System.Collections.Generic;
using FairyGUI;

namespace RedDot
{
    /// <summary>
    /// Binds FairyGUI components to red dots and takes care of the lifetime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RedDotBridge.Subscribe"/> is the raw seam: it registers a handle and
    /// expects somebody to unregister it. This class is what makes that safe to use from
    /// UI code, where components are disposed by screen teardown and recycled by list
    /// pools in an order nobody controls.
    /// </para>
    /// <para>
    /// Three guarantees:
    /// </para>
    /// <list type="number">
    /// <item><b>One binding per component.</b> Binding a component that is already bound
    /// releases the previous binding first. That is what makes a pooled list row safe to
    /// reuse for a different mail: the old subscription is gone before the new one
    /// exists, so a recycled row can never light up for the row it used to be — and the
    /// keyed dot it was holding open is destroyed with it.</item>
    /// <item><b>Disposed components release themselves.</b> A component that leaves the
    /// stage while disposed unbinds immediately, and any update aimed at a disposed
    /// component unbinds it on the spot. The second path is the one that matters, because
    /// a component disposed before it ever reached the stage never raises the first.</item>
    /// <item><b>The active flag is honoured.</b> <see cref="SetRedDotActive"/> is an
    /// external kill switch, and the badge shows only when the rule says yes
    /// <em>and</em> the screen says yes.</item>
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
        /// Binds the badge inside <paramref name="component"/> to the dot identified by
        /// <paramref name="type"/> and <paramref name="keys"/>. The current value is
        /// pushed immediately, so the badge is right on the frame it appears rather than
        /// on the next event.
        /// </summary>
        /// <returns>The view driving the badge.</returns>
        public RedDotView Bind(GComponent component, string type, params object[] keys)
        {
            return BindOwned(component, null, type, keys);
        }

        /// <summary>
        /// As <see cref="Bind"/>, with a grouping key — a screen name, a window, a list —
        /// that <see cref="UnbindAll"/> can release in one call.
        /// </summary>
        public RedDotView BindOwned(GComponent component, string owner, string type, params object[] keys)
        {
            if (component == null)
            {
                throw new ArgumentNullException(nameof(component));
            }

            if (string.IsNullOrEmpty(type))
            {
                throw new ArgumentException("A red dot binding needs a type name.", nameof(type));
            }

            if (component.isDisposed)
            {
                throw new ArgumentException("Cannot bind a disposed component.", nameof(component));
            }

            // The pooled-reuse guarantee: whatever this component was showing before, it
            // stops showing it now — and the keyed dot it was holding open goes with the
            // subscription.
            Unbind(component);

            var binding = new Binding(this, component, owner);
            _bindings.Add(component, binding);

            binding.Attach();
            binding.RegistryKey = _bridge.Subscribe(binding, type, keys);

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

        /// <summary>Releases every binding registered under <paramref name="owner"/>.</summary>
        public int UnbindAll(string owner)
        {
            if (string.IsNullOrEmpty(owner))
            {
                return 0;
            }

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
                Release(binding, unregisterFromLua: true);
            }

            return stale.Count;
        }

        /// <summary>
        /// The external kill switch: the badge shows only when the rule value and this
        /// are both true.
        /// </summary>
        /// <remarks>
        /// For the cases a rule should not have to know about — a tutorial step that
        /// suppresses every badge but one, a tab locked until a level, a screen fading
        /// out. Encoding those in the rule would mix presentation into the data model and
        /// make the rule untestable on its own.
        /// </remarks>
        public bool SetRedDotActive(GComponent component, bool active)
        {
            if (component == null || !_bindings.TryGetValue(component, out var binding))
            {
                return false;
            }

            binding.SetActive(active);
            return true;
        }

        public bool IsRedDotActive(GComponent component)
        {
            return component != null && _bindings.TryGetValue(component, out var binding) && binding.Active;
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

        /// <summary>The registry key a component is currently bound to, or null.</summary>
        public string KeyOf(GComponent component)
        {
            return component != null && _bindings.TryGetValue(component, out var binding)
                ? binding.RegistryKey
                : null;
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

            if (unregisterFromLua && binding.RegistryKey != null)
            {
                _bridge.Unsubscribe(binding.RegistryKey, binding);
            }
        }

        /// <summary>
        /// The handle the engine actually holds. It sits in front of the view so that an
        /// update aimed at a disposed component releases the binding instead of poking a
        /// dead object, and so the active flag can veto a value without the rule knowing.
        /// </summary>
        private sealed class Binding : IRedDotHandle
        {
            private readonly RedDotBinder _binder;
            private readonly EventCallback0 _onRemovedFromStage;

            public Binding(RedDotBinder binder, GComponent component, string owner)
            {
                _binder = binder;
                Component = component;
                Owner = owner;
                View = new RedDotView(component);
                Active = true;

                _onRemovedFromStage = OnRemovedFromStage;
            }

            public GComponent Component { get; }

            public string Owner { get; }

            public RedDotView View { get; }

            /// <summary>Set by Subscribe; null only during the call that creates it.</summary>
            public string RegistryKey { get; set; }

            public bool Active { get; private set; }

            /// <summary>What the rule last said, before the active flag is applied.</summary>
            public bool RuleValue { get; private set; }

            public void Attach()
            {
                Component.onRemovedFromStage.Add(_onRemovedFromStage);
            }

            public void Detach()
            {
                Component.onRemovedFromStage.Remove(_onRemovedFromStage);
            }

            public void SetRedDot(string registryKey, bool value)
            {
                if (Component.isDisposed)
                {
                    _binder.Release(this, unregisterFromLua: true);
                    return;
                }

                RegistryKey = registryKey;
                RuleValue = value;
                View.SetRedDot(registryKey, value && Active);
            }

            public void SetActive(bool active)
            {
                if (Active == active)
                {
                    return;
                }

                Active = active;
                if (!Component.isDisposed)
                {
                    View.SetRedDot(RegistryKey, RuleValue && Active);
                }
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
