using FairyGUI;
using UnityEngine;

namespace RedDot.Demo
{
    /// <summary>
    /// The two bits of global FairyGUI setup the demo and the tests both need.
    /// </summary>
    public static class FairyGuiEnvironment
    {
        /// <summary>
        /// Fonts FairyGUI will try, in order, when a text field does not name one.
        /// </summary>
        /// <remarks>
        /// This has to be set before the first text field exists. FairyGUI's last-resort
        /// fallback is <c>Resources.GetBuiltinResource(typeof(Font), "Arial.ttf")</c>,
        /// which Unity 6 no longer serves, and the failure is an exception rather than an
        /// ugly-looking label. Naming real OS fonts keeps it on the
        /// <c>CreateDynamicFontFromOSFont</c> path instead.
        /// </remarks>
        public const string DefaultFonts = "Arial, Segoe UI, Helvetica, Liberation Sans";

        private static bool _fontConfigured;

        /// <summary>Idempotent; safe to call from every scene load and every test setup.</summary>
        public static void EnsureDefaultFont()
        {
            if (_fontConfigured && !string.IsNullOrEmpty(UIConfig.defaultFont))
            {
                return;
            }

            if (string.IsNullOrEmpty(UIConfig.defaultFont))
            {
                UIConfig.defaultFont = DefaultFonts;
            }

            _fontConfigured = true;
        }
    }
}
