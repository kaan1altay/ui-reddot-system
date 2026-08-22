# FairyGUI package spec — `RedDotDemo`

Authoring instructions for the demo's UI package. The C# binds everything by name, so
the names below are a contract: match them and the demo works with no code changes.

Until this package exists the demo runs on a code-built fallback UI and says so in the
console. Nothing here changes any C#.

- **Package name:** `RedDotDemo`
- **Design resolution:** 750 × 1334 (portrait)
- **Export target:** `Assets/FairyGUI-Packages/` (see [Export](#export))
- **Editor project:** `FGUIProject/` in the repository root — already created; it
  currently holds the default `Package1`, which you can rename to `RedDotDemo`.

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

| Child name | Component | Title | Bound to |
| --- | --- | --- | --- |
| `btnMail` | TabButton | "Mail" | `Main.Mail` |
| `btnQuests` | TabButton | "Quests" | `Main.Quests` |
| `btnShop` | TabButton | "Shop" | `Main.Shop` |
| `btnApplyPatch` | ActionButton | "Apply Lua patch" | — |
| `btnStartOffer` | ActionButton | "Start limited offer" | — |
| `btnDumpTree` | ActionButton | "Dump tree" | — |
| `txtDebug` | text (optional) | — | — |

`txtDebug` is the demo's log panel: multi-line, left-aligned, roughly 690 × 560, not
touchable. Leave it out and the demo simply logs to the console instead.

### `MailScreen`

| Child name | Component | Title | Bound to |
| --- | --- | --- | --- |
| `btnInbox` | TabButton | "Inbox" | `Main.Mail.Inbox` |
| `btnSystem` | TabButton | "System" | `Main.Mail.System` |
| `btnAddMail` | ActionButton | "Add mail" | — |
| `btnReadOne` | ActionButton | "Read one" | — |
| `btnClaimAll` | ActionButton | "Claim all" | — |
| `btnBack` | ActionButton | "Back" | — |

### `QuestsScreen`

| Child name | Component | Title | Bound to |
| --- | --- | --- | --- |
| `btnDaily` | TabButton | "Daily" | `Main.Quests.Daily` |
| `btnAchievements` | TabButton | "Achievements" | `Main.Quests.Achievements` |
| `btnCompleteQuest` | ActionButton | "Complete a quest" | — |
| `btnClaimQuest` | ActionButton | "Claim a quest" | — |
| `btnUnlockAchievement` | ActionButton | "Unlock an achievement" | — |
| `btnBack` | ActionButton | "Back" | — |

### `ShopScreen`

| Child name | Component | Title | Bound to |
| --- | --- | --- | --- |
| `btnDailyDeals` | TabButton | "Daily deals" | `Main.Shop.DailyDeals` |
| `btnLimitedOffer` | TabButton | "Limited offer" | `Main.Shop.LimitedOffer` |
| `btnNewDeal` | ActionButton | "New deal arrives" | — |
| `btnBack` | ActionButton | "Back" | — |

> `Main.Shop.LimitedOffer` has no rule until the example Lua patch is applied. Binding a
> path that does not exist yet is legal and reads as hidden, so this badge sits dark
> until the patch lands and then lights up. That is the point of it.

---

## Export

1. In the FairyGUI Editor: **File → Publish Settings**
   - **Path:** `C:\SampleProjects\ui-reddot-system\Assets\FairyGUI-Packages`
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

## Committing the FairyGUI project

`FGUIProject/` is the editable source of the package and belongs in the repository — it
is a few hundred kilobytes of XML and it lets anyone opening the repo see how the UI was
built. Its own `.gitignore` already excludes the editor's `.objs` scratch folder. It is
currently untracked because it still holds the default empty `Package1`; commit it once
the real package is in there.
