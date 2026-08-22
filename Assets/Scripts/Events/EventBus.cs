using System;
using System.Collections.Generic;

namespace RedDot.Events
{
    /// <summary>
    /// A tiny string-keyed publish/subscribe bus.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The red dot system treats events as <em>signals</em>, not as data carriers: a
    /// rule always re-reads the authoritative value from its context instead of
    /// applying a delta carried by the event. That is what makes it safe to collapse
    /// a hundred "mail.received" events in one frame into a single evaluation, so the
    /// payload here is a plain optional string used for logging and debugging.
    /// </para>
    /// <para>
    /// The bus is bridged into Lua by <see cref="RedDotBridge"/>: Lua registers
    /// interest in the event names its rules name as triggers, and the bridge
    /// forwards exactly those events across the boundary. Only strings ever cross.
    /// </para>
    /// </remarks>
    public sealed class EventBus
    {
        private readonly Dictionary<string, List<Subscription>> _subscriptions =
            new Dictionary<string, List<Subscription>>(StringComparer.Ordinal);

        private readonly Dictionary<int, string> _tokenToEvent = new Dictionary<int, string>();

        private int _nextToken = 1;

        /// <summary>Total number of <see cref="Publish"/> calls, including ones nobody listened to.</summary>
        public int PublishCount { get; private set; }

        /// <summary>Number of published events that had at least one subscriber.</summary>
        public int DeliveredCount { get; private set; }

        private readonly struct Subscription
        {
            public readonly int Token;
            public readonly Action<string, string> Handler;

            public Subscription(int token, Action<string, string> handler)
            {
                Token = token;
                Handler = handler;
            }
        }

        /// <summary>
        /// Registers <paramref name="handler"/> for <paramref name="eventName"/> and returns
        /// a token to pass back to <see cref="Unsubscribe"/>.
        /// </summary>
        public int Subscribe(string eventName, Action<string, string> handler)
        {
            if (string.IsNullOrEmpty(eventName))
            {
                throw new ArgumentException("Event name must not be empty.", nameof(eventName));
            }

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (!_subscriptions.TryGetValue(eventName, out var handlers))
            {
                handlers = new List<Subscription>(1);
                _subscriptions[eventName] = handlers;
            }

            var token = _nextToken++;
            handlers.Add(new Subscription(token, handler));
            _tokenToEvent[token] = eventName;
            return token;
        }

        /// <summary>
        /// Removes the subscription created for <paramref name="token"/>. Unsubscribing an
        /// unknown or already-removed token is a no-op, because teardown order in UI code
        /// is rarely under anyone's control.
        /// </summary>
        public bool Unsubscribe(int token)
        {
            if (!_tokenToEvent.TryGetValue(token, out var eventName))
            {
                return false;
            }

            _tokenToEvent.Remove(token);

            if (!_subscriptions.TryGetValue(eventName, out var handlers))
            {
                return false;
            }

            for (var i = handlers.Count - 1; i >= 0; i--)
            {
                if (handlers[i].Token != token)
                {
                    continue;
                }

                handlers.RemoveAt(i);
                if (handlers.Count == 0)
                {
                    _subscriptions.Remove(eventName);
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Delivers <paramref name="eventName"/> to its subscribers.
        /// </summary>
        /// <remarks>
        /// Handlers are copied before delivery so that a handler may subscribe or
        /// unsubscribe while it runs, and a throwing handler is reported through
        /// <see cref="HandlerFailed"/> rather than stopping the remaining handlers.
        /// </remarks>
        public void Publish(string eventName, string payload = null)
        {
            PublishCount++;

            if (string.IsNullOrEmpty(eventName) ||
                !_subscriptions.TryGetValue(eventName, out var handlers) ||
                handlers.Count == 0)
            {
                return;
            }

            DeliveredCount++;

            var snapshot = handlers.ToArray();
            foreach (var subscription in snapshot)
            {
                try
                {
                    subscription.Handler(eventName, payload);
                }
                catch (Exception exception)
                {
                    HandlerFailed?.Invoke(eventName, exception);
                }
            }
        }

        /// <summary>Raised when a subscriber throws. Nothing else swallows the exception.</summary>
        public event Action<string, Exception> HandlerFailed;

        public int SubscriberCount(string eventName)
        {
            return _subscriptions.TryGetValue(eventName, out var handlers) ? handlers.Count : 0;
        }

        public bool HasSubscribers(string eventName)
        {
            return SubscriberCount(eventName) > 0;
        }

        /// <summary>Event names with at least one subscriber, sorted, for diagnostics and tests.</summary>
        public IReadOnlyList<string> SubscribedEvents()
        {
            var names = new List<string>(_subscriptions.Count);
            foreach (var pair in _subscriptions)
            {
                if (pair.Value.Count > 0)
                {
                    names.Add(pair.Key);
                }
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }

        public void Clear()
        {
            _subscriptions.Clear();
            _tokenToEvent.Clear();
            PublishCount = 0;
            DeliveredCount = 0;
        }
    }
}
