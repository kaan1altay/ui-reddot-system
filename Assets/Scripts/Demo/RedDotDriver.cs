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

        /// <summary>Nodes changed by the most recent flush. Handy on a debug overlay.</summary>
        public int LastChangeCount { get; private set; }

        /// <summary>Flushes that actually had work to do since this driver started.</summary>
        public int WorkingFlushes { get; private set; }

        public void Attach(RedDotBridge bridge)
        {
            _bridge = bridge;
        }

        public void Detach()
        {
            _bridge = null;
        }

        private void LateUpdate()
        {
            if (_bridge == null)
            {
                return;
            }

            LastChangeCount = _bridge.Flush();
            if (LastChangeCount > 0)
            {
                WorkingFlushes++;
            }
        }
    }
}
