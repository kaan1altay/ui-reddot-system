# Status

Last updated: 2026-08-22 — end of Slice 2.

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
| `RedDot.Runtime` | `Assets/Scripts` — bridge, view layer, event bus, context, demo |
| `RedDot.Tests.EditMode` | `Assets/Tests/EditMode` |
| `RedDot.Tests.PlayMode` | `Assets/Tests/PlayMode` |

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
  front, so a downloaded `reddot/rules.lua` shadows the shipped one. The demo's "Apply
  Lua patch" button is the other half of the same seam.
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

## What Slice 2 covers

The view layer, a playable demo, and the authoring spec for the UI package.

### The badge contract

`RedDotView` drives one FairyGUI badge purely by convention, so a designer can add a
badge to any component without a programmer touching anything. Inside a host component
it looks for a child named `redDot`, and inside that a controller named `state` with the
pages `hidden` / `dot` / `count`, plus an optional text field named `count`.

| Engine state | Page | Why |
| --- | --- | --- |
| hidden | `hidden` | — |
| visible, count 0 | `dot` | what an `any` policy parent reports: deliberately countless |
| visible, count 1 | `dot` | a lone "1" beside an icon is decoration, not information |
| visible, count 2–99 | `count` | the number says something |
| visible, count > 99 | `count`, text `99+` | so the badge never has to be wider than two digits |

Every piece is optional and every missing piece degrades instead of throwing: no
controller falls back to plain visibility, no `count` field falls back to the dot, no
`redDot` child at all makes the view inert. A half-authored UI package is a normal state
during authoring and it must not take the screen down.

The full authoring instructions are in [PACKAGE_SPEC.md](PACKAGE_SPEC.md).

### Binding lifetime

`RedDotBinder` is what makes the raw `RedDotBridge.Bind` seam safe to use from UI code,
where components are disposed by screen teardown and recycled by list pools in an order
nobody controls. It guarantees two things:

- **One binding per component.** Binding a component that is already bound releases the
  previous binding first, so a pooled row rebound to a different node can never still
  light up for the node it used to be. This is the regression the suite exists for.
- **Disposed components release themselves.** A component that leaves the stage while
  disposed unbinds immediately; and any update aimed at a disposed component unbinds it
  on the spot. The second path is the one that matters, because a component disposed
  before it ever reached the stage never raises the first.

`UnbindAll(owner)` releases a whole screen in one call, through the Lua binder's own
owner index.

### The demo

`Assets/Scenes/RedDotDemo.unity` — a fake game with four screens. What C# knows about
the badge tree is one table of `(screen, child, path)` rows in `DemoMain`; adding a badge
is a row, and adding a *rule* needs no C# at all.

- **Main** — Mail / Quests / Shop tabs, each with a badge on the aggregate node, plus the
  debug panel.
- **Mail / Quests / Shop** — leaf badges and buttons that poke the fake services. Opening
  a leaf section marks it seen. Reaching the Mail tab deliberately does not: that is not
  the same as having read the notice inside it.
- **Apply Lua patch** — loads `Assets/Lua/patches/rules_patch_example.lua` through
  `ReloadRules`. It introduces `Main.Shop.LimitedOffer`, a node no C# file mentions, and
  the Shop tab lights up through it. **Start limited offer** raises
  `LimitedOfferStarted`, the event the patch subscribed to as part of the reload's
  subscription diff — before the patch, nobody listened to it and it cost a dictionary
  miss. **Dump tree** logs `debugDump()`.

`RedDotDriver` is the entire per-frame cost of the system: one `Flush()` in `LateUpdate`,
which returns immediately when nothing is dirty. It runs late so badges settle once, at
the end of the frame that caused them, instead of flickering through intermediate states.

### Graceful missing-package mode

The authored `RedDotDemo` package is now in the repository, so this is no longer the
default path -- but it stays load-bearing. When the package is absent `DemoMain` builds
the same screens in code and says so:

```
[RedDotDemo] UI package 'RedDotDemo' not found -- using fallback UI.
Author the package per docs/PACKAGE_SPEC.md and export it to Assets/FairyGUI-Packages/
```

`DemoUIFactory` builds real `GComponent`s with a real `state` controller and real display
gears, so the fallback behaves exactly like an authored package rather than approximating
one. That makes it both the placeholder UI and the EditMode tests' fixtures: the view
tests drive a controller that really hides real children. A package that is only partly
authored also degrades per screen — a missing component falls back to its code-built
version and logs which one.

### Files added

```
Assets/Scripts/RedDot/
  RedDotView.cs             badge adapter: page selection, "99+" cap, graceful degrading
  RedDotBinder.cs           component-scoped binding with pooled-reuse and disposal safety

Assets/Scripts/Demo/
  DemoMain.cs               boot, screen flow, the (screen, child, path) table, debug panel
  DemoUIFactory.cs          the code-built screens: fallback UI and test fixtures
  RedDotDriver.cs           one Flush per frame, in LateUpdate
  DemoLogPanel.cs           the on-screen log: scrolling list, text field, or console
  FairyGuiEnvironment.cs    default font setup (Unity 6 no longer serves Arial.ttf)

Assets/Editor/
  DemoSceneBuilder.cs       generates the demo scene; RedDot > Rebuild demo scene

Assets/Lua/patches/
  rules_patch_example.lua   the live-ops patch that adds Main.Shop.LimitedOffer

Assets/Scenes/RedDotDemo.unity
Assets/Tests/EditMode/RedDotViewTests.cs
Assets/Tests/PlayMode/RedDotDemoSmokeTests.cs
```

### One landmine worth recording

A handle must implement `IRedDotHandle` **implicitly**, as a public method. Lua reaches a
handle by member name through xLua reflection, so an explicit interface implementation —
a private method under a mangled name — compiles, binds, and then silently never fires.
The interface's own documentation now says so, and the binder tests would have caught it
again.

## Test results

**66 / 66 EditMode**, 39.2 s, and **2 / 2 PlayMode**, 3.5 s. Both run headless, and the
PlayMode pair now runs against the authored UI package.

```
Unity 6000.0.59f2, NUnit 3.5.0
EditMode  total="66" passed="66" failed="0" inconclusive="0" skipped="0"
PlayMode  total="2"  passed="2"  failed="0" inconclusive="0" skipped="0"
```

Every test drives the real Lua modules through the real bridge; the only doubles are the
fake game managers and the in-memory seen store.

### Slice 1 — the engine (35 cases)

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

### Slice 2 — the view layer and the demo log (31 cases)

| Area | Cases |
| --- | --- |
| Page selection | hidden selects `hidden` and the gear really removes the artwork, count 1 stays a dot, count 0 stays a dot, count > 1 selects `count`, 99 vs 100 vs 41235 → `99`/`99+`/`99+`, and a badge cycling back and forth between all three |
| Degrading | no `count` field falls back to the dot while still recording the state, no `state` controller falls back to plain visibility, no `redDot` child is inert, a disposed host is not touched |
| Binding | current state is pushed on bind, a bound badge follows the engine, unbind is idempotent, `UnbindAll` releases one screen and leaves the others |
| Pooled reuse | **rebinding a recycled component drops the old binding entirely** — the stale view never hears about the old node again — and rebinding to the same path does not stack callbacks |
| Disposal | a disposed component releases itself on the next update, and can also be swept explicitly |
| Hot update | a badge bound to a path with no rule lights up when the example patch introduces it |
| Debug log | the scrolling list wins over the text field which wins over the console, placeholder items are cleared on boot, one item per line, the oldest drop past the cap (custom and the default 100), a list whose item cannot be built degrades to the text field, and the code-built fallback UI still logs |

### Slice 2 — the demo scene (2 PlayMode cases)

The scene loads, the FairyGUI root comes up, the main screen is on it and its Mail button
is bound. Three mails arrive and the badge does **not** move — events only mark nodes
dirty — and by the next frame the driver has flushed and it reads `3`. The second case
applies the example patch and watches the Shop tab light up through
`Main.Shop.LimitedOffer`.

### Running the tests

```
"C:\Program Files\Unity\Hub\Editor\6000.0.59f2\Editor\Unity.exe" ^
  -batchmode -nographics ^
  -projectPath C:\SampleProjects\ui-reddot-system ^
  -runTests -testPlatform EditMode ^
  -testResults C:\SampleProjects\ui-reddot-system\TestResults\results.xml ^
  -logFile -
```

Swap `EditMode` for `PlayMode` for the smoke tests. Or open the project and use
**Window → General → Test Runner**.

> **Note on the recorded runs.** Unity refuses batchmode on a project another Editor
> instance has open, and the Editor was open on this project at the time. The recorded
> runs therefore executed against a byte-identical copy of `Assets/`, `Packages/` and
> `ProjectSettings/` in a scratch directory. **Close the Editor** and the command above
> runs against the repository directly.

## Next slices

### Slice 3 — the authored package, and the polish pass

- Drop in the `RedDotDemo` package authored per [PACKAGE_SPEC.md](PACKAGE_SPEC.md) and
  confirm the demo switches out of fallback mode. Nothing in C# should need to change;
  if anything does, that is a bug in the spec and worth recording.
- Commit `FGUIProject/` once it holds the real package.
- Lay the screens out properly now that there is art to lay out, and give the debug panel
  a live `debugDump()` rather than an action log.
- Record the hot-update flow end to end for the GIF: main screen dark → **Apply Lua
  patch** → Shop tab lights up → Shop screen shows the new row → tap it → clears →
  **Start limited offer** → back again. That sequence is the single most convincing thing
  in the repository and deserves to be captured carefully.
- A "Revert patch" button (`ReloadRulesFromModule`) so the demo can be shown twice in a
  row without restarting.

### Slice 4 — README, GIFs and cleanup

- README: the pitch, an architecture diagram, the 60-second tour, the GIFs from Slice 3.
- Drop the URP template leftovers (`Assets/Readme.asset`, `Assets/TutorialInfo/`,
  `Assets/Scenes/SampleScene.unity`) — deliberately left alone so far to keep the diffs
  about the red dot system.
- A GitHub Actions workflow running the EditMode tests (needs a Unity licence secret; the
  alternative is documenting the local command, which is already done above).
- A short note on what changes when this ships on a device: where the Lua lives
  (StreamingAssets or an AssetBundle instead of `Assets/Lua`), where the UI package lives
  (`Resources` or an AssetBundle instead of `Assets/FairyGUI-Packages`), and the xLua code
  generation step for IL2CPP.

## Anything needing your eyes

- **Nothing is blocking.** Slice 2 is complete and green, and the demo is playable now on
  the fallback UI.
- **The UI package is yours to author**: [PACKAGE_SPEC.md](PACKAGE_SPEC.md) has the exact
  component and child names, the badge's controller and gears, the publish settings, and
  how to verify the demo switched out of fallback mode. `FGUIProject/` is already created
  and still holds the default `Package1`; it is untracked until it holds the real thing.
- **Close the Unity Editor** before running the batchmode test command, or it will refuse
  the project lock.
- Two assembly definitions were added inside the vendored xLua tree (`XLua`,
  `XLua.Editor`). That is a local modification to a dependency, recorded in
  `Assets/XLua/VENDORED.md`. It is unavoidable: an asmdef assembly cannot reference the
  default `Assembly-CSharp`, where an asmdef-less xLua would land.
