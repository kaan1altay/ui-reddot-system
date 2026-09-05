# FairyGUI package spec — `RedDotDemo`

Authoring instructions for the demo's UI package. The C# binds everything by name, so
the names below are a contract: match them and the demo works with no code changes.

The package is authored and in the repository; this file is the contract it satisfies,
and what to add when the demo grows. Anything missing degrades to a code-built fallback
with a console warning naming the child, so a half-finished package is always playable.
Nothing here changes any C#.

- **Package name:** `RedDotDemo`
- **Design resolution:** 750 × 1334 (portrait)
- **Export target:** `Assets/FairyGUI-Packages/` (see [Export](#export))
- **Editor project:** `FGUIProject/` in the repository root, committed alongside the
  published package.

Visual design is entirely yours. Flat rectangles are fine; the reference implementation
in `Assets/Scripts/Demo/DemoUIFactory.cs` uses nothing but filled rects and ellipses.

---

## 1. `RedDotBadge` — the reusable badge

The only component whose internals the code actually inspects. Everything else is
layout.

**Size:** 46 × 46 (any size works; the code never reads it)

**Controller** — this is the part that matters:

| | |
| --- | --- |
| Name | `state` |
| Pages, in order | `hidden`, `dot`, `count` |

**Children:**

| Name | Type | Purpose |
| --- | --- | --- |
| `dot` | image / graph | The red circle. Name is not read by code; call it what you like. |
| `count` | **text** | The number. **Name must be exactly `count`.** |

**Gears** — set these on the children, in the *Gear: display* row of the inspector:

| Object | Controller | Pages ticked |
| --- | --- | --- |
| the dot artwork | `state` | `dot`, `count` |
| `count` | `state` | `count` |

So on page `hidden` nothing shows, on `dot` only the circle shows, and on `count` the
circle and the number show.

Set the count text centred, bold, small. It never holds more than three characters —
the view caps display at `99+`.

> **What the code does with this.** `RedDotView` sets `state.selectedPage` to `hidden`,
> `dot` or `count` and writes `count.text`. It never touches visibility directly, so the
> gears above are what actually shows and hides the artwork. If you leave the controller
> out entirely the view falls back to toggling the badge's own visibility, and if you
> leave the `count` text out it just never shows a number — but the badge will look
> better if both are there.

---

## 2. `TabButton` — a button that can carry a badge

**Size:** 220 × 150

**Children:**

| Name | Type | Notes |
| --- | --- | --- |
| `title` | text | The label. Set per instance. |
| `redDot` | instance of `RedDotBadge` | **Name must be exactly `redDot`.** Place it top-right. |

Extension: leave as **None**. Setting it to *Button* also works — a `GButton` is a
`GComponent` and the code only ever calls `GetChild` and `onClick` — but a plain
component is one less thing to get wrong.

## 3. `ActionButton` — a plain button, no badge

**Size:** 690 × 78

**Children:** `title` (text).

---

## 4. Screens

All four are 750 × 1334 and **must be marked "export"** in the FairyGUI Editor —
`UIPackage.CreateObject` can only build exported components.

Layout is up to you. Only the names below are load-bearing. Every button is an instance
of `TabButton` or `ActionButton` as marked; the `title` of each is just a label, quoted
here so the demo reads sensibly.

### `Main`

| Child name | Component | Title | Watches |
| --- | --- | --- | --- |
| `btnMail` | TabButton | "Mail" | `Mail` |
| `btnQuests` | TabButton | "Quests" | `Quests` |
| `btnShop` | TabButton | "Shop" | `Shop` |
| `btnApplyPatch` | ActionButton | "Apply Lua patch" | — |
| `btnStartOffer` | ActionButton | "Start limited offer" | — |
| `btnAdvanceDay` | ActionButton | "Advance time +1 day" | — |
| `btnReconcile` | ActionButton | "Reconcile: off" | — |
| `btnDumpTree` | ActionButton | "Dump state" | — |
| `listDebugText` | list (optional) | — | — |
| `txtDebug` | text (optional) | — | — |

The demo writes its running commentary to the first of these it finds, and to the Unity
console if it finds neither.

`listDebugText` is the good one: a **non-virtual** `GList` with `overflow: scroll`, whose
default item is `DebugTextListItem`. The demo appends one item per line, caps the list at
100 by dropping the oldest, and scrolls to the newest. Placeholder items left inside it in
the editor are cleared on boot, so the mock-up text never shows at runtime. A *virtual*
list is ignored with a warning — those are driven by `numItems` and an item renderer
rather than by appending children.

`txtDebug` is the simpler alternative: one multi-line text field, roughly 690 × 560, not
touchable, showing the last twelve lines. It is what the code-built fallback UI uses.

### `DebugTextListItem` — one log line

**Must be exported.** Extension **Label**, holding a text field named `title` with
`autoSize: height`, and a relation from the component to that text
(`width-width, height-height`) so the item grows to fit a long line. The demo writes
through the label's title.

If the list names no default item, the demo falls back to whatever item is already inside
it, and then to a component named `DebugTextListItem` looked up in the package. If none of
those resolve it says so in the console and drops to `txtDebug`.

> Note: the item's text field has UBB and variable parsing available in the editor. The
> demo's own lines contain no markup, but `DumpState()` output does contain square
> brackets — if you ever route that into the list rather than the console, turn UBB off on
> the item or the brackets will be eaten as markup.


### `MailScreen`

| Child name | Component | Title | Watches |
| --- | --- | --- | --- |
| `btnInbox` | TabButton | "Inbox" | `Mail` |
| `btnSystem` | TabButton | "System" | `MailItem|1` |
| `listMail` | list | — | one `MailItem|<id>` per row |
| `btnAddMail` | ActionButton | "Add mail" | — |
| `btnClaimAll` | ActionButton | "Claim all" | — |
| `btnBack` | ActionButton | "Back" | — |

`btnInbox` watches the **same global dot** the main screen's Mail button does — one dot,
two subscribers — which is worth seeing on screen because nothing about the model makes
that a special case.

`listMail` is the keyed-lifecycle demo and needs authoring; see below. Until it exists
the screen still works, and the demo says so in the console.

> The old `btnReadOne` is gone: mail is opened by tapping a row now. Leaving it in the
> package is harmless — the demo only wires the children it finds.

#### `listMail` — the mail list

A **non-virtual** `GList`, `overflow: scroll`, roughly 690 × 460, whose default item is
`MailListItem`. The demo fills it on entering the screen and empties it on leaving.
Nothing else about it is load-bearing: no controller, no selection mode, no item
renderer.

#### `MailListItem` — one mail row

**Must be exported.** Roughly 690 × 72, with two children:

| Name | Type | Notes |
| --- | --- | --- |
| `title` | text | The subject line. The demo writes `#12  Season rewards` into it. |
| `redDot` | instance of `RedDotBadge` | **Name must be exactly `redDot`.** Pin it to the right-hand edge. |

Leave the extension as **None** and make the component itself touchable — the demo adds
the click handler that opens the mail. The badge is set `touchable = false` in code, so
it can never swallow the tap meant for the row underneath it.

Each row binds `MailItem|<mailId>` when it is created and releases it when the list is
cleared, which is what destroys the dot. Watch the demo's log line on the way in and out
of the screen: the live dot count goes up by one per row and comes back down again.

### `QuestsScreen`

| Child name | Component | Title | Watches |
| --- | --- | --- | --- |
| `btnDaily` | TabButton | "Daily" | `QuestItem|1|1` |
| `btnAchievements` | TabButton | "Achievements" | `QuestItem|2|7` |
| `btnCompleteQuest` | ActionButton | "Complete the daily" | — |
| `btnClaimQuest` | ActionButton | "Claim the daily" | — |
| `btnUnlockAchievement` | ActionButton | "Unlock the achievement" | — |
| `btnBack` | ActionButton | "Back" | — |

These two are the **two-key** case: a `QuestItem` is identified by a chapter and a quest,
in that order, and the registry key is `QuestItem|2|7`.

Both tabs are **tappable, and tapping one claims that quest** -- the same interaction as
tapping a mail row to read it. A `QuestItem` rule asks whether the quest is claimable, so
claiming is the only thing that can turn its dot off; marking it seen does nothing,
because it reports real game state rather than whether the player has looked. Every quest
you can put on screen needs an action that claims it, or its badge lights once and stays
lit.

### `ShopScreen`

| Child name | Component | Title | Watches |
| --- | --- | --- | --- |
| `btnDailyDeals` | TabButton | "Daily deals" | `Shop` |
| `btnLimitedOffer` | TabButton | "Limited offer" | `LimitedOffer` |
| `btnNewDeal` | ActionButton | "A free deal arrives" | — |
| `btnBack` | ActionButton | "Back" | — |

> `LimitedOffer` is a **type** no rule defines until the example Lua patch is applied.
> Binding a type that does not exist yet is legal and reads as off, so this badge sits
> dark until the patch lands and then lights up. That is the point of it.


---

## Export

1. In the FairyGUI Editor: **File → Publish Settings**
   - **Path:** `C:\<repo>\ui-reddot-system\Assets\FairyGUI-Packages`
   - **Type:** Unity
   - **Binary format:** on (this is what produces `RedDotDemo_fui.bytes`)
2. **File → Publish** (Ctrl+P).

You should end up with:

```
Assets/FairyGUI-Packages/
  RedDotDemo_fui.bytes
  RedDotDemo_atlas0.png
```

The runtime here is FairyGUI **5.2.0**, so publish from a FairyGUI Editor of the 5.x /
2022+ generation.

### Where the loader looks

`DemoMain` tries these in order and takes the first that exists:

| Path | Works in |
| --- | --- |
| `Assets/FairyGUI-Packages/RedDotDemo` | the Editor (FairyGUI resolves `Assets/…` through `AssetDatabase`) |
| `RedDotDemo` — i.e. `Assets/Resources/RedDotDemo_fui.bytes` | the Editor **and** player builds |
| `UI/RedDotDemo` — i.e. `Assets/Resources/UI/…` | the Editor **and** player builds |

The first is the one to use while authoring. A player build cannot read from
`Assets/`, so if you ever want to build the demo out, publish to a `Resources` folder
instead (or load the package from an AssetBundle — the loader is three lines).

---

## Verifying it took

1. Open `Assets/Scenes/RedDotDemo.unity` and press Play.
2. The console should read:

   ```
   [RedDotDemo] loaded UI package from 'Assets/FairyGUI-Packages/RedDotDemo'
   ```

   If it instead says *"UI package 'RedDotDemo' not found -- using fallback UI"*, the
   files are not where the loader looks. Check the publish path and that the package is
   named `RedDotDemo`.
3. Anything named wrong reports itself specifically, one line per problem:

   ```
   [RedDotDemo] screen 'MailScreen' has no child 'btnInbox'; that badge will not update.
   [RedDotDemo] package 'RedDotDemo' has no component 'QuestsScreen'; falling back ...
   ```

   A screen that is missing from the package falls back to its code-built version on its
   own, so a partly-authored package is still playable.
4. Click **Add mail** three times on the Mail screen. The Inbox badge shows `3`, the
   Mail tab on the main screen shows `3`, and the root aggregates it. Click **Apply Lua
   patch** on the main screen and the Shop tab lights up.

## The FairyGUI project

`FGUIProject/` is the editable source of the package and is committed — a few hundred
kilobytes of XML that lets anyone opening the repo see how the UI was built. Its own
`.gitignore` excludes the editor's `.objs` scratch folder. Re-publish after any change,
or Unity keeps loading the last export.
