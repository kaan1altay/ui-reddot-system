using System;
using System.Collections.Generic;
using FairyGUI;
using UnityEngine;

namespace RedDot.Demo
{
    /// <summary>Where a <see cref="DemoLogPanel"/> ended up writing.</summary>
    public enum DemoLogTarget
    {
        /// <summary>Nothing on screen took it; lines go to the Unity console.</summary>
        Console,

        /// <summary>A plain <c>txtDebug</c> text field showing the last few lines.</summary>
        TextField,

        /// <summary>A scrollable <c>listDebugText</c> list, one item per line.</summary>
        List,
    }

    /// <summary>
    /// The demo's on-screen log, resolved against whatever the UI package actually offers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three targets, tried in order: a scrollable <c>listDebugText</c> list, a plain
    /// <c>txtDebug</c> text field, and the Unity console. Every step down is a degrade,
    /// not a failure — the demo has to stay playable against a package that is half
    /// authored, and against the code-built fallback UI, which has neither widget.
    /// </para>
    /// <para>
    /// The list is the good one: it scrolls, it keeps a real backlog, and its item
    /// component sizes its own height, so a long <c>debugDump</c> line wraps instead of
    /// being clipped.
    /// </para>
    /// </remarks>
    public sealed class DemoLogPanel
    {
        /// <summary>Preferred: a scrollable list, one item per line.</summary>
        public const string ListChildName = "listDebugText";

        /// <summary>Fallback: a single text field holding the last few lines.</summary>
        public const string TextChildName = "txtDebug";

        /// <summary>The list's item component, when the list does not name a default item.</summary>
        public const string ItemComponentName = "DebugTextListItem";

        /// <summary>Lines kept in the list before the oldest are dropped.</summary>
        public const int DefaultMaxItems = 100;

        /// <summary>Lines kept in the text field, which cannot scroll.</summary>
        private const int TextFieldLines = 12;

        private readonly Action<string> _console;
        private readonly List<string> _buffer = new List<string>();

        private readonly GList _list;
        private readonly GTextField _text;

        /// <summary>Item resource url, when items come from the list's own pool.</summary>
        private readonly string _itemUrl;

        private readonly Func<GObject> _createItem;

        /// <summary>
        /// Resolves the best available target inside <paramref name="screen"/>.
        /// </summary>
        /// <param name="packageName">
        /// UI package to look <see cref="ItemComponentName"/> up in, when the list itself
        /// does not say what its items are.
        /// </param>
        /// <param name="console">Where the last-resort lines go. Defaults to <see cref="Debug.Log(object)"/>.</param>
        /// <param name="itemFactory">
        /// Creates one unattached list item. Left null in the demo, where items come from
        /// the list's pool; supplied by tests, which have no UI package to pull from.
        /// </param>
        public DemoLogPanel(
            GComponent screen,
            string packageName = null,
            Action<string> console = null,
            Func<GObject> itemFactory = null)
        {
            _console = console ?? (line => Debug.Log(line));

            if (screen != null)
            {
                _list = screen.GetChild(ListChildName) as GList;
                _text = screen.GetChild(TextChildName) as GTextField;
            }

            if (_list != null && _list.isVirtual)
            {
                // A virtual list is driven by numItems and an item renderer, not by
                // AddChild. Supporting both shapes is not worth it for a debug panel.
                Debug.LogWarning(
                    "[RedDotDemo] '" + ListChildName + "' is a virtual list; the debug log needs a plain one.");
                _list = null;
            }

            if (_list != null)
            {
                _createItem = itemFactory;
                if (_createItem == null)
                {
                    _itemUrl = ResolveItemUrl(_list, packageName);
                    if (_itemUrl != null)
                    {
                        _createItem = () => _list.GetFromPool(_itemUrl);
                    }
                }

                if (_createItem == null)
                {
                    Debug.LogWarning(
                        "[RedDotDemo] '" + ListChildName + "' has no default item and the package has no '" +
                        ItemComponentName + "'; falling back to '" + TextChildName + "'.");
                    _list = null;
                }
                else
                {
                    // Whatever the designer left in the list is a placeholder.
                    Clear();
                }
            }

            Target = _list != null ? DemoLogTarget.List
                : _text != null ? DemoLogTarget.TextField
                : DemoLogTarget.Console;
        }

        /// <summary>Which of the three targets this panel resolved to.</summary>
        public DemoLogTarget Target { get; }

        /// <summary>Lines kept before the oldest is dropped. Only meaningful for the list.</summary>
        public int MaxItems { get; set; } = DefaultMaxItems;

        /// <summary>Lines currently on screen.</summary>
        public int LineCount => _list != null ? _list.numChildren : _buffer.Count;

        /// <summary>The newest line's text, or null when nothing has been written.</summary>
        public string LastLine
        {
            get
            {
                if (_list != null)
                {
                    return _list.numChildren > 0 ? ReadItemText(_list.GetChildAt(_list.numChildren - 1)) : null;
                }

                return _buffer.Count > 0 ? _buffer[_buffer.Count - 1] : null;
            }
        }

        public void Append(string line)
        {
            switch (Target)
            {
                case DemoLogTarget.List:
                    AppendToList(line);
                    break;

                case DemoLogTarget.TextField:
                    AppendToTextField(line);
                    break;

                default:
                    _console(line);
                    break;
            }
        }

        public void Clear()
        {
            _buffer.Clear();

            if (_list != null)
            {
                while (_list.numChildren > 0)
                {
                    RemoveOldest();
                }
            }

            if (_text != null)
            {
                _text.text = string.Empty;
            }
        }

        #region Targets

        private void AppendToList(string line)
        {
            var item = _createItem();
            if (item == null)
            {
                // The pool could not build the item after all; do not lose the line.
                _console(line);
                return;
            }

            _list.AddChild(item);
            WriteItemText(item, line);

            while (_list.numChildren > MaxItems)
            {
                RemoveOldest();
            }

            // Items size their own height, so the list's bounds are stale until it is
            // asked to catch up -- and scrolling to a stale bound scrolls to the wrong
            // place.
            _list.EnsureBoundsCorrect();

            if (_list.numChildren > 0)
            {
                _list.ScrollToView(_list.numChildren - 1);
            }
        }

        private void AppendToTextField(string line)
        {
            _buffer.Add(line);
            if (_buffer.Count > TextFieldLines)
            {
                _buffer.RemoveRange(0, _buffer.Count - TextFieldLines);
            }

            _text.text = string.Join("\n", _buffer);
        }

        private void RemoveOldest()
        {
            if (_itemUrl != null)
            {
                // Pool-owned: give it back so the next line reuses it.
                _list.RemoveChildToPoolAt(0);
            }
            else
            {
                _list.RemoveChildAt(0, true);
            }
        }

        #endregion

        #region Item plumbing

        /// <summary>
        /// Where list items come from, in order of how much the package has already said:
        /// the list's own default item, then whatever the designer left inside it, then
        /// the item component looked up by name.
        /// </summary>
        private static string ResolveItemUrl(GList list, string packageName)
        {
            if (!string.IsNullOrEmpty(list.defaultItem))
            {
                return list.defaultItem;
            }

            if (list.numChildren > 0 && !string.IsNullOrEmpty(list.GetChildAt(0).resourceURL))
            {
                return list.GetChildAt(0).resourceURL;
            }

            if (!string.IsNullOrEmpty(packageName))
            {
                var url = UIPackage.GetItemURL(packageName, ItemComponentName);
                if (!string.IsNullOrEmpty(url))
                {
                    return url;
                }
            }

            return null;
        }

        /// <summary>
        /// <c>GLabel</c> maps <c>text</c> onto its title, and so do <c>GTextField</c> and
        /// <c>GButton</c>. Only a bare component ignores it, and then the title child is
        /// the next place to look.
        /// </summary>
        private static void WriteItemText(GObject item, string line)
        {
            item.text = line;
            if (item.text == line)
            {
                return;
            }

            if (item is GComponent component && component.GetChild("title") is GTextField title)
            {
                title.text = line;
            }
        }

        private static string ReadItemText(GObject item)
        {
            if (item.text != null)
            {
                return item.text;
            }

            return item is GComponent component ? (component.GetChild("title") as GTextField)?.text : null;
        }

        #endregion
    }
}
