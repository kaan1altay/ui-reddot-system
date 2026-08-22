using UnityEngine;

namespace RedDot.Demo
{
    /// <summary>
    /// Calls <see cref="RedDotBridge.Flush"/> once per frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the entire per-frame cost of the red dot system. A flush with nothing dirty
    /// returns without evaluating a single rule, so on a quiet frame this is one Lua call
    /// and a comparison against zero.
    /// </para>
    /// <para>
    /// It runs in <c>LateUpdate</c> so that everything a frame's gameplay did has already
    /// happened: badges settle once, at the end of the frame that caused them, instead of
    /// flickering through intermediate states.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class RedDotDriver : MonoBehaviour
    {
        private RedDotBridge _bridge;
        private FakeClock _clock;

        /// <summary>Nodes changed by the most recent flush. Handy on a debug overlay.</summary>
        public int LastChangeCount { get; private set; }

        /// <summary>Flushes that actually had work to do since this driver started.</summary>
        public int WorkingFlushes { get; private set; }

        /// <param name="clock">
        /// Advanced by the frame delta before each flush, so the demo's game time runs at
        /// the usual rate and a scheduled reset fires because the clock passed it.
        /// </param>
        public void Attach(RedDotBridge bridge, FakeClock clock = null)
        {
            _bridge = bridge;
            _clock = clock;
        }

        public void Detach()
        {
            _bridge = null;
            _clock = null;
        }

        private void LateUpdate()
        {
            if (_bridge == null)
            {
                return;
            }

            _clock?.Advance(Time.deltaTime);

            LastChangeCount = _bridge.Flush();
            if (LastChangeCount > 0)
            {
                WorkingFlushes++;
            }
        }
    }
}
