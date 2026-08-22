# ui-reddot-system

A data-driven, hot-updatable **red dot (badge) notification system** for live-service
mobile UIs — the rules and the tree live in **Lua (xLua)**, the views are **FairyGUI**,
and the C# in between knows nothing about any particular badge.

> **Status: Slice 1 complete.** The engine, the bridge and 35 passing EditMode tests are
> in. The FairyGUI views and the demo scene are next. See [docs/STATUS.md](docs/STATUS.md).

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
  subscriptions and re-evaluates. A patch file can add a brand-new badge, live, with
  zero C# changes — bindings survive because they hold path strings, not objects.
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

## Layout

| Path | What lives there |
| --- | --- |
| `Assets/Lua/reddot/` | the engine: tree, rules, seen store, manager, binder |
| `Assets/Scripts/` | the bridge, the event bus, the game-data context, fake game managers |
| `Assets/Tests/EditMode/` | 35 NUnit cases driving the real Lua through the real bridge |
| `docs/STATUS.md` | versions, design decisions, test results, what comes next |

## Running the tests

```
"C:\Program Files\Unity\Hub\Editor\6000.0.59f2\Editor\Unity.exe" ^
  -batchmode -nographics -projectPath . ^
  -runTests -testPlatform EditMode -testResults TestResults\results.xml -logFile -
```

Or **Window → General → Test Runner → EditMode → Run All** in the Editor.

## Dependencies

Vendored, runtime only, with the parts that were left out recorded next to them:

- [xLua](https://github.com/Tencent/xLua) v2.1.16 — MIT — `Assets/XLua/VENDORED.md`
- [FairyGUI](https://github.com/fairygui/FairyGUI-unity) 5.2.0 — MIT — `Assets/FairyGUI/VENDORED.md`

Built with Unity 6000.0.59f2.

## License

MIT — see [LICENSE](LICENSE).
