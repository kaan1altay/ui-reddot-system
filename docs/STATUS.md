# Status

Last updated: 2026-08-22 — end of Slice 1.

## Environment

| | |
| --- | --- |
| Unity | **6000.0.59f2** (Unity 6 LTS), URP mobile template |
| Editor path | `C:\Program Files\Unity\Hub\Editor\6000.0.59f2\Editor\Unity.exe` |
| xLua | **v2.1.16** (Tencent, MIT) — see [`Assets/XLua/VENDORED.md`](../Assets/XLua/VENDORED.md) |
| FairyGUI | **5.2.0** (MIT) — see [`Assets/FairyGUI/VENDORED.md`](../Assets/FairyGUI/VENDORED.md) |
| Lua VM | `Assets/Plugins/x86_64/xlua.dll` (desktop x64; add other platform plugins from the upstream release when you target them) |
| git / gh | 2.55.0 / 2.95.0 |

Only the desktop x64 native plugin and the runtime source of each dependency are
vendored, so the repository stays small. Both `VENDORED.md` files record exactly what
was left out and why.

## Assemblies

| Assembly | Contents |
| --- | --- |
| `XLua`, `XLua.Editor` | vendored, with two assembly definitions added locally so project code can reference them |
| `FairyGUI` | vendored, upstream asmdef used as-is |
| `RedDot.Runtime` | `Assets/Scripts` — bridge, event bus, context, fake game services |
| `RedDot.Tests.EditMode` | `Assets/Tests/EditMode` |

## What Slice 1 covers

The complete engine, with no UI attached yet.

### The contract

- **Node** — a path-addressed point in a tree (`Main.Mail.Inbox`). Parents aggregate
  children. A parent is visible whenever any child is visible; the per-node **policy**
  only decides the number it shows:
  - `sum` — add the visible children's counts (`Main`, `Main.Mail`)
  - `max` — show the largest visible child count (`Main.Quests`, so two unrelated lists
    do not add up to a meaningless total)
  - `any` — a dot with no number (`Main.Shop`)
- **Rule** — leaf nodes only, declared in Lua as data: `mode`, `triggers`, `evaluate(ctx)`.
  - `Persistent` — visible while the condition holds; marking it seen does nothing
    (`Main.Mail.Inbox`, `Main.Quests.Daily`)
  - `TransientUntilSeen` — visible until the player looks; a later trigger makes it
    unseen again (`Main.Mail.System`, `Main.Quests.Achievements`, `Main.Shop.DailyDeals`)
- **Dirty batching** — events mark leaves dirty and evaluate nothing. One `flush()` per
  frame evaluates each dirty leaf exactly once, bubbles aggregates deepest-first and
  stops on any branch where nothing moved, then notifies only the nodes that changed.
  A flush with an empty dirty set does no work at all: there is no polling anywhere.
- **Hot-update seam** — `manager:reloadRules(newRules)` diffs the event subscriptions
  (events that survive are never churned; events that lost their last rule are really
  unsubscribed on the C# bus) and re-evaluates the whole tree, reporting only genuine
  differences. Bindings survive because they hold path strings, not node objects.

### Design decisions worth calling out

- **Events are signals, not payloads.** A rule always re-reads the authoritative value
  from `ctx`. That is what makes it correct to collapse a hundred `mail.received` events
  in one frame into a single evaluation, instead of applying a hundred deltas that can
  drift out of sync with the real inbox.
- **Nothing but strings, booleans and numbers crosses the Lua/C# boundary.** No Lua
  table is marshalled into C# and no C# delegate is handed to Lua, so the bridge needs
  no generated glue and the whole seam fits in one file.
- **`RedDotContext` is the honest limit of hot updates.** A patch can invent any badge
  from the accessors it lists, with zero C# change — but it cannot invent a new
  accessor. `ctx:Counter(key)` is the deliberate escape hatch for values live-ops sends
  after the client shipped.
- **The loader searches an ordered list of roots.** Roots registered as patches go in
  front, so a downloaded `reddot/rules.lua` shadows the shipped one. Slice 3 turns this
  into the "Apply patch" button.
- **C# knows nothing about any specific badge.** Search the C# sources for "mail" and
  the only hits are the fake game managers and the test fixture.

### Files

```
Assets/Lua/reddot/
  types.lua        node paths, aggregation policies, rule modes, declared tree
  tree.lua         path strings -> parent/child tree; validation
  rules.lua        the rule table: pure data, the only file that knows what a badge means
  seen_store.lua   TransientUntilSeen flags + the persistence backend contract
  manager.lua      the engine: dirty batching, aggregation, seen logic, reloadRules
  binder.lua       bind(path, handle) / unbind / unbindAll(owner)

Assets/Scripts/
  Events/EventBus.cs        string-keyed pub/sub, bridged into Lua
  RedDot/RedDotBridge.cs    boots xLua; RaiseEvent / Flush / MarkSeen / ReloadRules
  RedDot/LuaScriptLoader.cs ordered search roots, patch folders shadow base files
  RedDot/RedDotContext.cs   the game-data surface rules may read
  RedDot/SeenPersistence.cs ISeenPersistence + in-memory and PlayerPrefs stores
  Demo/FakeGameServices.cs  fake mail / quest / shop managers

Assets/Tests/EditMode/
  RedDotCoreTests.cs        35 NUnit cases
```

## Test results

**35 / 35 passing**, 35.3 s total (the fuzz case is 1.4 s of it).

```
Unity 6000.0.59f2, EditMode, NUnit 3.5.0
total="35" passed="35" failed="0" inconclusive="0" skipped="0"
```

Every test drives the real Lua modules through the real bridge; the only doubles are
the fake game managers and the in-memory seen store. Coverage:

| Area | Cases |
| --- | --- |
| Tree and aggregation | start state, declared shape, `sum`, `max`, `any`, cross-branch root, parent goes dark only when every child does |
| Rule modes | persistent ignores mark-seen, persistent clears with its condition, transient hides once seen, trigger re-arms a seen transient, mark-seen on a parent clears the subtree, unknown path is a no-op, seen state survives a restart through the C# persistence callback |
| Dirty batching | 10 events → 1 evaluation, idle flush does nothing at all, unchanged nodes do not notify, bubbling stops where nothing changed, unsubscribed events never reach Lua, the bridge subscribes to exactly the named triggers |
| Bindings | immediate push on bind, follow-up changes, unbind is idempotent, `unbindAll` by owner, several handles per path |
| Hot update | patch adds a brand-new badge with no C# change, a binding made *before* the reload picks up the node it introduces, a retired rule really unsubscribes on the C# bus, bindings survive, a reload reports only badges that moved, module reload goes through the loader, a rule on an aggregate is rejected without half-applying, a throwing rule is contained |
| Diagnostics | `debugDump` renders the tree, the seen set and the stats |
| Fuzz | 10 000 random events, seen marks and reloads in both directions |

The fuzz run: 10 000 events → 986 flushes, 21 hot reloads, 17 202 dispatches, but only
**4 545 rule evaluations** — batching in one number. After every flush and every reload
it asserts that each parent equals the aggregate of its children under its policy, and
that the set of notified paths is exactly the set of paths whose state differs: nothing
silent, nothing spurious, nothing notified twice.

### Running the tests

```
"C:\Program Files\Unity\Hub\Editor\6000.0.59f2\Editor\Unity.exe" ^
  -batchmode -nographics ^
  -projectPath C:\SampleProjects\ui-reddot-system ^
  -runTests -testPlatform EditMode ^
  -testResults C:\SampleProjects\ui-reddot-system\TestResults\results.xml ^
  -logFile -
```

Or open the project and use **Window → General → Test Runner → EditMode → Run All**.

> **Note on the recorded run.** Unity refuses batchmode on a project another Editor
> instance has open, and the Editor was open on this project at the time. The recorded
> run therefore executed against a byte-identical copy of `Assets/`, `Packages/` and
> `ProjectSettings/` in a scratch directory. Close the Editor and the command above
> runs against the repository directly.

## Next slices

### Slice 2 — FairyGUI views and the demo scene

- `RedDotView`, a `MonoBehaviour`/`GComponent` adapter implementing `IRedDotHandle`:
  finds the badge child object in a FairyGUI component, sets its visibility and count
  text, and binds/unbinds on enable/disable with the screen as its owner.
- A `RedDotDriver` component that calls `Flush()` once per frame in `LateUpdate`.
- A demo scene with a `UIPanel` and the main-menu layout (bottom tab bar: Mail, Quests,
  Shop; each tab opening a sub-screen with its own rows).
- A **UI package spec** in `docs/` for you to author in the FairyGUI Editor: component
  names, the badge component's structure (`dot` graphic + `count` text + a controller
  for dot/number/none), and the export settings the runtime expects. The `.fui` binary
  is authored in the FairyGUI desktop app, so that step needs your hands.
- Add `FairyGUI` to `RedDot.Runtime.asmdef`'s references (deliberately left out while
  Slice 1 had no UI, so a FairyGUI problem could not block the core).

### Slice 3 — fake-game demo wiring and the hot-update patch demo

- Wire the fake mail/quest/shop services to on-screen buttons: "receive mail",
  "read one", "complete a daily", "refresh deals", "roll over the day".
- A debug overlay rendering `debugDump()` live, so the tree, the seen set and the
  batching stats are visible while clicking.
- An **Apply patch** button that registers `Assets/LuaPatch` as a patch root and calls
  `ReloadRulesFromModule()`. The patch file adds a `Main.Shop.Bundles` badge driven by
  `ctx:Counter("shop.bundles")` — a new badge, live, with zero C# changes and no
  domain reload.
- A **Revert patch** button, so the demo can be shown twice in a row.

### Slice 4 — polish

- README: the pitch, an architecture diagram, the 60-second tour, and GIFs of the demo
  and of the hot patch landing.
- Drop the URP template leftovers (`Assets/Readme.asset`, `Assets/TutorialInfo/`) and
  rename `SampleScene` — deliberately left alone so far to keep the Slice 1 diffs about
  the red dot system.
- A GitHub Actions workflow running the EditMode tests (needs a Unity licence secret;
  the alternative is documenting the local command, which is already done above).
- A short note on what changes when this ships on a device: where the Lua lives
  (StreamingAssets or an AssetBundle instead of `Assets/Lua`), and the xLua code
  generation step for IL2CPP.

## Anything needing your eyes

- **Nothing is blocking.** Slice 1 is complete and green.
- The FairyGUI `.fui` UI package for Slice 2 has to be authored in the FairyGUI Editor
  desktop app. Slice 2 will produce the spec; building the package is your step.
- Two assembly definitions were added inside the vendored xLua tree (`XLua`,
  `XLua.Editor`). That is a local modification to a dependency, recorded in
  `Assets/XLua/VENDORED.md`. It is unavoidable: an asmdef assembly cannot reference the
  default `Assembly-CSharp`, where an asmdef-less xLua would land.
