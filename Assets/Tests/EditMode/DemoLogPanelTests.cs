using System;
using System.Collections.Generic;
using FairyGUI;
using NUnit.Framework;
using RedDot.Demo;

namespace RedDot.Tests
{
    /// <summary>
    /// Tests for the demo's on-screen log and, more to the point, for the order in which
    /// it degrades.
    /// </summary>
    /// <remarks>
    /// The list's items normally come out of its own pool, which needs a UI package. These
    /// tests supply an item factory instead, so the append, cap and clear behaviour is
    /// exercised against a real <c>GList</c> without one. Resolving the item url from a
    /// package is the one path that cannot be covered here; the fixture covers what
    /// happens when that resolution fails, which is the case that would otherwise break a
    /// package silently.
    /// </remarks>
    [TestFixture]
    public sealed class DemoLogPanelTests
    {
        private readonly List<GComponent> _screens = new List<GComponent>();
        private readonly List<string> _console = new List<string>();

        [SetUp]
        public void SetUp()
        {
            FairyGuiEnvironment.EnsureDefaultFont();
            _console.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var screen in _screens)
            {
                if (!screen.isDisposed)
                {
                    screen.Dispose();
                }
            }

            _screens.Clear();
        }

        /// <summary>Builds a stand-in for the Main screen with the widgets asked for.</summary>
        private GComponent NewScreen(bool withList, bool withTextField, int placeholderItems = 0)
        {
            var screen = new GComponent { name = "Main" };
            screen.SetSize(750, 1334);
            _screens.Add(screen);

            if (withList)
            {
                var list = new GList { name = DemoLogPanel.ListChildName };
                list.SetSize(690, 560);
                for (var i = 0; i < placeholderItems; i++)
                {
                    list.AddChild(NewItem("placeholder " + i));
                }

                screen.AddChild(list);
            }

            if (withTextField)
            {
                screen.AddChild(DemoUIFactory.CreateOutputPanel(DemoLogPanel.TextChildName, 690, 560));
            }

            return screen;
        }

        private static GTextField NewItem(string text = "")
        {
            var item = new GTextField { name = "item" };
            item.SetSize(690, 30);
            item.text = text;
            return item;
        }

        private DemoLogPanel NewPanel(GComponent screen, bool withItemFactory = true)
        {
            return new DemoLogPanel(
                screen,
                packageName: null,
                console: line => _console.Add(line),
                itemFactory: withItemFactory ? () => NewItem() : (Func<GObject>)null);
        }

        private static GList ListOf(GComponent screen)
        {
            return (GList)screen.GetChild(DemoLogPanel.ListChildName);
        }

        #region Target selection

        [Test]
        public void TheScrollingListWinsOverTheTextField()
        {
            var panel = NewPanel(NewScreen(withList: true, withTextField: true));

            Assert.That(panel.Target, Is.EqualTo(DemoLogTarget.List));
        }

        [Test]
        public void ATextFieldIsUsedWhenThereIsNoList()
        {
            var panel = NewPanel(NewScreen(withList: false, withTextField: true));

            Assert.That(panel.Target, Is.EqualTo(DemoLogTarget.TextField));
        }

        [Test]
        public void TheConsoleTakesOverWhenTheScreenHasNeither()
        {
            var panel = NewPanel(NewScreen(withList: false, withTextField: false));
            Assert.That(panel.Target, Is.EqualTo(DemoLogTarget.Console));

            panel.Append("something happened");

            Assert.That(_console, Is.EqualTo(new[] { "something happened" }),
                "the line has to go somewhere");
        }

        [Test]
        public void AListWithNoItemToBuildFallsBackToTheTextField()
        {
            // No default item, nothing inside it, and no package to look the item
            // component up in: exactly what a mistyped item name looks like.
            var screen = NewScreen(withList: true, withTextField: true);
            var panel = NewPanel(screen, withItemFactory: false);

            Assert.That(panel.Target, Is.EqualTo(DemoLogTarget.TextField),
                "a list it cannot fill is worse than a text field it can");

            panel.Append("still logged");
            Assert.That(panel.LastLine, Is.EqualTo("still logged"));
        }

        [Test]
        public void TheCodeBuiltFallbackUIStillLogsToItsTextField()
        {
            // The fallback screens have a txtDebug and no list, and must be unaffected by
            // any of this.
            var screen = DemoUIFactory.CreateScreen("Main", "Red dot demo");
            _screens.Add(screen);
            screen.AddChild(DemoUIFactory.CreateOutputPanel(DemoLogPanel.TextChildName, 690, 560));

            var panel = new DemoLogPanel(screen, DemoMain.PackageName, line => _console.Add(line));

            Assert.That(panel.Target, Is.EqualTo(DemoLogTarget.TextField));
            panel.Append("fallback UI (see docs/PACKAGE_SPEC.md)");
            Assert.That(panel.LastLine, Is.EqualTo("fallback UI (see docs/PACKAGE_SPEC.md)"));
        }

        #endregion

        #region The list

        [Test]
        public void EachLineBecomesOneListItem()
        {
            var screen = NewScreen(withList: true, withTextField: false);
            var panel = NewPanel(screen);

            panel.Append("first");
            panel.Append("second");
            panel.Append("third");

            var list = ListOf(screen);
            Assert.That(list.numChildren, Is.EqualTo(3));
            Assert.That(panel.LineCount, Is.EqualTo(3));
            Assert.That(list.GetChildAt(0).text, Is.EqualTo("first"));
            Assert.That(list.GetChildAt(2).text, Is.EqualTo("third"));
            Assert.That(panel.LastLine, Is.EqualTo("third"),
                "the newest line is the one at the bottom, which is what gets scrolled to");
        }

        [Test]
        public void ThePlaceholderItemsTheDesignerLeftBehindAreCleared()
        {
            var screen = NewScreen(withList: true, withTextField: false, placeholderItems: 2);
            Assert.That(ListOf(screen).numChildren, Is.EqualTo(2), "authored placeholders are real children");

            var panel = NewPanel(screen);

            Assert.That(panel.LineCount, Is.Zero, "the demo starts with an empty log, not with the mock-up text");

            panel.Append("first real line");
            Assert.That(panel.LineCount, Is.EqualTo(1));
            Assert.That(panel.LastLine, Is.EqualTo("first real line"));
        }

        [Test]
        public void TheOldestLinesAreDroppedPastTheCap()
        {
            var screen = NewScreen(withList: true, withTextField: false);
            var panel = NewPanel(screen);
            panel.MaxItems = 5;

            for (var i = 1; i <= 8; i++)
            {
                panel.Append("line " + i);
            }

            var list = ListOf(screen);
            Assert.That(list.numChildren, Is.EqualTo(5));
            Assert.That(list.GetChildAt(0).text, Is.EqualTo("line 4"), "the first three fell off the top");
            Assert.That(panel.LastLine, Is.EqualTo("line 8"));
        }

        [Test]
        public void TheDefaultCapIsAHundredLines()
        {
            var screen = NewScreen(withList: true, withTextField: false);
            var panel = NewPanel(screen);

            for (var i = 1; i <= 120; i++)
            {
                panel.Append("line " + i);
            }

            Assert.That(panel.LineCount, Is.EqualTo(DemoLogPanel.DefaultMaxItems));
            Assert.That(ListOf(screen).GetChildAt(0).text, Is.EqualTo("line 21"));
            Assert.That(panel.LastLine, Is.EqualTo("line 120"));
        }

        [Test]
        public void ClearingEmptiesTheList()
        {
            var screen = NewScreen(withList: true, withTextField: false);
            var panel = NewPanel(screen);
            panel.Append("one");
            panel.Append("two");

            panel.Clear();

            Assert.That(panel.LineCount, Is.Zero);
            Assert.That(panel.LastLine, Is.Null);
        }

        #endregion

        #region The text field

        [Test]
        public void TheTextFieldShowsEveryLineItStillHolds()
        {
            var screen = NewScreen(withList: false, withTextField: true);
            var panel = NewPanel(screen);

            panel.Append("first");
            panel.Append("second");

            var text = ((GTextField)screen.GetChild(DemoLogPanel.TextChildName)).text;
            Assert.That(text, Is.EqualTo("first\nsecond"));
            Assert.That(panel.LastLine, Is.EqualTo("second"));
        }

        [Test]
        public void TheTextFieldKeepsOnlyTheLinesThatFit()
        {
            var screen = NewScreen(withList: false, withTextField: true);
            var panel = NewPanel(screen);

            for (var i = 1; i <= 30; i++)
            {
                panel.Append("line " + i);
            }

            Assert.That(panel.LineCount, Is.EqualTo(12),
                "a text field cannot scroll, so it holds a screenful and no more");
            Assert.That(panel.LastLine, Is.EqualTo("line 30"));

            var text = ((GTextField)screen.GetChild(DemoLogPanel.TextChildName)).text;
            Assert.That(text, Does.StartWith("line 19"));
            Assert.That(text, Does.EndWith("line 30"));
        }

        #endregion
    }
}
