# ui-reddot-system

A data-driven, hot-updatable **red dot (badge) notification system** for live-service
mobile UIs — the rules and the tree live in **Lua (xLua)**, the views are **FairyGUI**,
and the C# in between knows nothing about any particular badge.

> **Status: Slice 2 complete.** The engine, the FairyGUI view layer, the authored UI
> package and a playable demo scene are in, with 66 EditMode and 2 PlayMode tests green.
> See [docs/STATUS.md](docs/STATUS.md).

## The idea

Every live-service game grows a red dot system, and it is always the same three
problems: the rules change more often than the client ships, the tree of badges has to
aggregate correctly, and naive implementations poll every frame.

This sample answers all three:

```lua
-- Assets/Lua/reddot/rules.lua -- the only file that knows what a badge means
[types.MAIL_INBOX] = {
    mode     = types.MODE_PERSISTENT,
    triggers = { "mail.received", "mail.read", "mail.deleted" },
    evaluate = function(ctx) return ctx.Mail:UnreadCount() end,
},
```

- **Data, not code.** A badge is a table: a mode, the events that make it dirty, and a
  function that reads game state. Adding one is a data change.
- **Hot-updatable.** `reloadRules` swaps the table at runtime, diffs the event
  subscriptions and re-evaluates. The demo has a button that does exactly this: it loads
  a patch file which adds a badge no C# file mentions, and watches the Shop tab light up
  through it. Bindings survive because they hold path strings, not objects.
- **Batched, never polled.** Events mark nodes dirty; one flush per frame evaluates each
  dirty leaf exactly once and notifies only the nodes that actually changed. In the fuzz
  test, 10 000 events cost 4 545 rule evaluations and zero spurious notifications.

## The tree

```
Main                        sum
├── Mail                    sum
│   ├── Inbox               Persistent          unread mail
│   └── System              TransientUntilSeen  operational notice
├── Quests                  max
│   ├── Daily               Persistent          completable dailies
│   └── Achievements        TransientUntilSeen  newly unlocked
└── Shop                    any
    └── DailyDeals          TransientUntilSeen  new stock
```

A parent is visible whenever any child is visible; the policy only decides the number it
shows — `sum` adds, `max` picks the most urgent, `any` shows a dot with no number.

## The view layer

A badge is a FairyGUI component named `redDot` carrying a controller named `state` with
the pages `hidden` / `dot` / `count`, and optionally a text field named `count`. The
adapter picks the page and writes the number; the controller's gears do the rest.

Counts of one stay a plain dot, counts over 99 read `99+`, and every missing piece
degrades rather than throws — a package with no count field simply never shows a number.
The full authoring contract is in [docs/PACKAGE_SPEC.md](docs/PACKAGE_SPEC.md).

## Running the demo

Open `Assets/Scenes/RedDotDemo.unity` and press Play. Tabs open sections, buttons poke
the fake mail / quest / shop services, and the debug panel applies the example Lua patch
live.

If the authored UI package is ever missing, the demo builds the same screens in code and
says so in the console. Everything is playable either way.

## Running the tests

```
"C:\Program Files\Unity\Hub\Editor\6000.0.59f2\Editor\Unity.exe" ^
  -batchmode -nographics -projectPath . ^
  -runTests -testPlatform EditMode -testResults TestResults\results.xml -logFile -
```

Swap `EditMode` for `PlayMode` for the scene smoke tests, or use
**Window → General → Test Runner** in the Editor. Batchmode needs the Editor closed.

## Layout

| Path | What lives there |
| --- | --- |
| `Assets/Lua/reddot/` | the engine: tree, rules, seen store, manager, binder |
| `Assets/Lua/patches/` | the example live-ops patch |
| `Assets/Scripts/RedDot/` | the bridge, the FairyGUI view and the binding lifetime |
| `Assets/Scripts/Demo/` | the demo bootstrap, the code-built UI, fake game managers |
| `Assets/Tests/` | 66 EditMode cases and 2 PlayMode smoke tests |
| `docs/STATUS.md` | versions, design decisions, test results, what comes next |
| `docs/PACKAGE_SPEC.md` | how to author the FairyGUI package the demo binds to |

## Dependencies

Vendored, runtime only, with the parts that were left out recorded next to them:

- [xLua](https://github.com/Tencent/xLua) v2.1.16 — MIT — `Assets/XLua/VENDORED.md`
- [FairyGUI](https://github.com/fairygui/FairyGUI-unity) 5.2.0 — MIT — `Assets/FairyGUI/VENDORED.md`

Built with Unity 6000.0.59f2.

## License

MIT — see [LICENSE](LICENSE).
