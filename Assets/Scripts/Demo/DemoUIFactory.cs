using System.Collections.Generic;
using FairyGUI;
using UnityEngine;

namespace RedDot.Demo
{
    /// <summary>
    /// Builds the demo screens in code, with exactly the structure and the child names
    /// that <c>docs/PACKAGE_SPEC.md</c> asks the FairyGUI Editor package for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists for two reasons. It keeps the demo playable before the <c>.fui</c>
    /// package is authored — a placeholder rectangle is not pretty, but it is honest
    /// about what the system does — and it gives the EditMode tests a real FairyGUI
    /// object tree to drive, gears and controllers included, without a UI package or a
    /// stage.
    /// </para>
    /// <para>
    /// It is deliberately dumb about layout: one column, fixed rows, no relations. The
    /// authored package is where design lives.
    /// </para>
    /// </remarks>
    public static class DemoUIFactory
    {
        public const int DesignWidth = 750;
        public const int DesignHeight = 1334;

        public const int TabWidth = 220;
        public const int TabHeight = 150;
        public const int ButtonWidth = 690;
        public const int ButtonHeight = 78;
        public const int BadgeSize = 46;

        private static readonly Color Background = new Color32(0x1B, 0x1E, 0x24, 0xFF);
        private static readonly Color TabFill = new Color32(0x2E, 0x34, 0x40, 0xFF);
        private static readonly Color ButtonFill = new Color32(0x3A, 0x44, 0x55, 0xFF);
        private static readonly Color BadgeFill = new Color32(0xE0, 0x3B, 0x3B, 0xFF);
        private static readonly Color Ink = new Color32(0xEC, 0xF0, 0xF5, 0xFF);

        #region Badge

        /// <summary>
        /// The reusable badge: a <c>state</c> controller with <c>hidden</c> / <c>dot</c> /
        /// <c>count</c> pages, a dot graphic gear-linked to the last two pages, and an
        /// optional count field gear-linked to the last one.
        /// </summary>
        /// <param name="withCountField">
        /// False builds a dot-only badge, which is what a package looks like when the
        /// designer has not added the number yet. <see cref="RedDotView"/> must cope.
        /// </param>
        public static GComponent CreateBadge(bool withCountField = true)
        {
            var badge = new GComponent { name = RedDotView.BadgeChildName };
            badge.SetSize(BadgeSize, BadgeSize);

            var state = new Controller { name = RedDotView.StateControllerName };
            state.AddPage(RedDotView.PageHidden);
            state.AddPage(RedDotView.PageDot);
            state.AddPage(RedDotView.PageCount);
            badge.AddController(state);

            var dot = new GGraph { name = "dot" };
            dot.DrawEllipse(BadgeSize, BadgeSize, BadgeFill);
            badge.AddChild(dot);
            LinkDisplay(dot, state, RedDotView.PageDot, RedDotView.PageCount);

            if (withCountField)
            {
                var count = new GTextField { name = RedDotView.CountChildName };
                count.SetSize(BadgeSize, BadgeSize);
                count.align = AlignType.Center;
                count.verticalAlign = VertAlignType.Middle;
                count.textFormat = new TextFormat { size = 24, color = Color.white, bold = true };
                count.text = string.Empty;
                badge.AddChild(count);
                LinkDisplay(count, state, RedDotView.PageCount);
            }

            state.selectedPage = RedDotView.PageHidden;
            return badge;
        }

        /// <summary>
        /// Wires a child's visibility to a controller, the way a display gear set in the
        /// FairyGUI Editor does.
        /// </summary>
        /// <remarks>
        /// The gear stores page <em>ids</em> rather than page names, so the names have to
        /// be translated. Getting this wrong is silent: the gear simply never matches and
        /// the child stays visible on every page.
        /// </remarks>
        private static void LinkDisplay(GObject child, Controller controller, params string[] pageNames)
        {
            var gear = (GearDisplay)child.GetGear(0);

            // Assigning the controller resets the gear, so pages must be set afterwards.
            gear.controller = controller;

            var ids = new string[pageNames.Length];
            for (var i = 0; i < pageNames.Length; i++)
            {
                ids[i] = controller.GetPageIdByName(pageNames[i]);
            }

            gear.pages = ids;
        }

        #endregion

        #region Buttons and screens

        /// <summary>
        /// A tab-style button: a filled rectangle, a <c>title</c> label and, optionally, a
        /// <c>redDot</c> badge pinned to its top right.
        /// </summary>
        public static GComponent CreateTabButton(string name, string title, bool withBadge = true)
        {
            var button = CreateButtonBase(name, title, TabWidth, TabHeight, TabFill, 26);

            if (withBadge)
            {
                var badge = CreateBadge();
                badge.SetXY(TabWidth - BadgeSize - 8, 8);
                button.AddChild(badge);
            }

            return button;
        }

        /// <summary>A wide, plain action button for the demo's game-state pokes.</summary>
        public static GComponent CreateActionButton(string name, string title)
        {
            return CreateButtonBase(name, title, ButtonWidth, ButtonHeight, ButtonFill, 24);
        }

        private static GComponent CreateButtonBase(
            string name, string title, int width, int height, Color fill, int fontSize)
        {
            var button = new GComponent { name = name };
            button.SetSize(width, height);
            button.touchable = true;

            var background = new GGraph { name = "bg" };
            background.DrawRect(width, height, 2, Ink, fill);
            button.AddChild(background);

            var label = new GTextField { name = "title" };
            label.SetSize(width, height);
            label.align = AlignType.Center;
            label.verticalAlign = VertAlignType.Middle;
            label.textFormat = new TextFormat { size = fontSize, color = Ink };
            label.text = title;
            label.touchable = false;
            button.AddChild(label);

            return button;
        }

        /// <summary>An empty screen with a background, ready to have rows dropped into it.</summary>
        public static GComponent CreateScreen(string name, string heading)
        {
            var screen = new GComponent { name = name };
            screen.SetSize(DesignWidth, DesignHeight);

            var background = new GGraph { name = "bg" };
            background.DrawRect(DesignWidth, DesignHeight, 0, Background, Background);
            screen.AddChild(background);

            var title = new GTextField { name = "heading" };
            title.SetSize(DesignWidth, 70);
            title.SetXY(0, 30);
            title.align = AlignType.Center;
            title.verticalAlign = VertAlignType.Middle;
            title.textFormat = new TextFormat { size = 34, color = Ink, bold = true };
            title.text = heading;
            title.touchable = false;
            screen.AddChild(title);

            return screen;
        }

        /// <summary>A read-only text panel; the demo's debug output goes here.</summary>
        public static GTextField CreateOutputPanel(string name, int width, int height)
        {
            var panel = new GTextField { name = name };
            panel.SetSize(width, height);
            panel.align = AlignType.Left;
            panel.verticalAlign = VertAlignType.Top;
            panel.textFormat = new TextFormat { size = 18, color = Ink };
            panel.text = string.Empty;
            panel.touchable = false;
            return panel;
        }

        #endregion

        #region Layout helper

        /// <summary>
        /// Stacks children down the screen. The whole layout system of the fallback UI,
        /// on purpose: the authored package is where anything prettier belongs.
        /// </summary>
        public sealed class Column
        {
            private readonly GComponent _screen;
            private float _y;

            public Column(GComponent screen, float startY)
            {
                _screen = screen;
                _y = startY;
            }

            public T Add<T>(T child, float gap = 16f) where T : GObject
            {
                child.SetXY((DesignWidth - child.width) / 2f, _y);
                _screen.AddChild(child);
                _y += child.height + gap;
                return child;
            }

            /// <summary>Places a set of tabs side by side and moves the cursor past them.</summary>
            public void AddRow(IList<GObject> children, float gap = 16f)
            {
                var total = 0f;
                foreach (var child in children)
                {
                    total += child.width;
                }

                total += gap * (children.Count - 1);

                var x = (DesignWidth - total) / 2f;
                var tallest = 0f;
                foreach (var child in children)
                {
                    child.SetXY(x, _y);
                    _screen.AddChild(child);
                    x += child.width + gap;
                    if (child.height > tallest)
                    {
                        tallest = child.height;
                    }
                }

                _y += tallest + gap;
            }

            public float Y => _y;
        }

        #endregion
    }
}
