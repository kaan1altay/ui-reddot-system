using System;
using FairyGUI;

namespace RedDot
{
    /// <summary>
    /// Drives one FairyGUI badge from red dot state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The adapter works by convention, so a designer can add a badge to any component
    /// in the FairyGUI Editor without a programmer touching anything. Inside the host
    /// component it looks for a child named <c>redDot</c>, and inside that:
    /// </para>
    /// <list type="bullet">
    /// <item>a controller named <c>state</c> with the pages <c>hidden</c>, <c>dot</c>
    /// and <c>count</c>, which is what actually shows and hides the artwork</item>
    /// <item>an optional text field named <c>count</c></item>
    /// </list>
    /// <para>
    /// Everything is optional. A component with no <c>redDot</c> child is inert, a badge
    /// with no controller falls back to plain visibility, and a badge with no count
    /// field simply never shows a number. Missing pieces degrade rather than throw,
    /// because a half-finished UI package is a normal state during authoring and it must
    /// not take the screen down.
    /// </para>
    /// <para>
    /// The view never polls. It only runs when the engine reports a change, which for a
    /// badge is a handful of times per session.
    /// </para>
    /// <para>
    /// See <c>docs/PACKAGE_SPEC.md</c> for the authoring side of this contract.
    /// </para>
    /// </remarks>
    public sealed class RedDotView : IRedDotHandle
    {
        /// <summary>Name of the badge child inside the host component.</summary>
        public const string BadgeChildName = "redDot";

        /// <summary>Name of the optional count text field inside the badge.</summary>
        public const string CountChildName = "count";

        /// <summary>Name of the controller that shows and hides the badge artwork.</summary>
        public const string StateControllerName = "state";

        public const string PageHidden = "hidden";
        public const string PageDot = "dot";
        public const string PageCount = "count";

        /// <summary>Above this the badge shows <see cref="OverflowText"/> instead of the number.</summary>
        public const int MaxDisplayCount = 99;

        public const string OverflowText = "99+";

        private readonly GComponent _host;
        private readonly GObject _badge;
        private readonly Controller _state;
        private readonly GTextField _countText;

        /// <summary>
        /// Wraps the badge inside <paramref name="host"/>. Resolution happens once, here,
        /// so the per-change path is three field reads and a page assignment.
        /// </summary>
        public RedDotView(GComponent host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));

            _badge = host.GetChild(BadgeChildName);

            var badgeComponent = _badge as GComponent;
            if (badgeComponent != null)
            {
                _state = badgeComponent.GetController(StateControllerName);
                _countText = badgeComponent.GetChild(CountChildName) as GTextField;
            }
        }

        /// <summary>The host component this view drives. Useful for lifetime checks.</summary>
        public GComponent Host => _host;

        /// <summary>False when the host has no <c>redDot</c> child; the view is then inert.</summary>
        public bool HasBadge => _badge != null;

        /// <summary>False when the badge cannot show a number, so counts collapse to a dot.</summary>
        public bool HasCountField => _countText != null;

        /// <summary>False when the badge has no <c>state</c> controller and falls back to visibility.</summary>
        public bool HasStateController => _state != null;

        /// <summary>The node path last applied, or null before the first update.</summary>
        public string Path { get; private set; }

        public bool Visible { get; private set; }

        public int Count { get; private set; }

        /// <summary>The controller page currently selected, or null when there is no controller.</summary>
        public string CurrentPage => _state != null ? _state.selectedPage : null;

        /// <summary>What the count field reads right now, or null when there is no field.</summary>
        public string CountText => _countText != null ? _countText.text : null;

        /// <summary>Entry point from the engine. Public on purpose: see <see cref="IRedDotHandle"/>.</summary>
        public void SetRedDot(string path, bool visible, int count)
        {
            Path = path;
            Apply(visible, count);
        }

        /// <summary>
        /// Shows, hides or re-numbers the badge.
        /// </summary>
        /// <remarks>
        /// A count of one stays a plain dot. "1" next to an icon reads as decoration
        /// rather than information, and the aggregate policies deliberately produce zero
        /// for dot-only nodes, so the number only appears when it says something.
        /// </remarks>
        public void Apply(bool visible, int count)
        {
            Visible = visible;
            Count = count < 0 ? 0 : count;

            if (_badge == null || _host.isDisposed)
            {
                return;
            }

            var showNumber = visible && Count > 1 && _countText != null;

            if (showNumber)
            {
                _countText.text = FormatCount(Count);
            }

            if (_state != null)
            {
                ApplyPage(visible, showNumber);
                return;
            }

            // No controller: the badge object itself is the switch.
            _badge.visible = visible;
            if (_countText != null)
            {
                _countText.visible = showNumber;
            }
        }

        /// <summary>
        /// Selects the page that matches the state, stepping down to something that exists
        /// when the package does not define the ideal one.
        /// </summary>
        private void ApplyPage(bool visible, bool showNumber)
        {
            if (!visible)
            {
                if (_state.HasPage(PageHidden))
                {
                    _badge.visible = true;
                    _state.selectedPage = PageHidden;
                }
                else
                {
                    // A package without a hidden page can still be hidden the blunt way.
                    _badge.visible = false;
                }

                return;
            }

            _badge.visible = true;

            if (showNumber && _state.HasPage(PageCount))
            {
                _state.selectedPage = PageCount;
                return;
            }

            if (_state.HasPage(PageDot))
            {
                _state.selectedPage = PageDot;
            }
        }

        /// <summary>Caps the displayed number so a wide badge never has to exist.</summary>
        public static string FormatCount(int count)
        {
            if (count > MaxDisplayCount)
            {
                return OverflowText;
            }

            return count.ToString();
        }
    }
}
