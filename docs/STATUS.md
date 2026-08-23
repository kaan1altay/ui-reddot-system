# Status

**Complete.** Last updated 2026-08-23.

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

## Architecture v2 — what changed, and why

Slice 3 replaced the core. Slices 1 and 2 built a **path tree with parent
aggregation**: `Main.Mail.Inbox` was a node, `Main.Mail` was the sum of its children,
and a badge bubbled upward. Play-testing killed it.

### What was wrong with aggregation

- **A parent could not be right until its children existed.** The Mail button on the
  lobby is a *lobby* concern, but under aggregation its value was a function of nodes
  that only came into being when the mail screen was built. On a cold start the lobby was
  correct only because every node was created at boot — which is the same as saying the
  tree could never be sparse.
- **Re-entering a screen showed empty badges.** Nodes were rebuilt on the way in, and
  nothing had a value until the next event bubbled through. This was the bug that
  actually got noticed.
- **The tree was a second model of the game.** `Main.Mail.Inbox` had to mirror both the
  UI hierarchy and the data hierarchy, and the two do not agree for long. A badge that
  belongs to two screens had to be either duplicated or awkwardly re-parented.
- **Aggregation policies were a decision nobody wanted.** `sum` / `max` / `any` existed
  because a parent had to say *something* about children it did not understand. In
  practice a designer wants "the Shop button lights up when there is a free deal", which
  is a sentence about the shop, not an arithmetic over its sub-screens.

### What replaced it

**Identity is a type plus ordered key values**, and the registry key is those joined with
`|`:

```
"Shop"            a global dot: no keys, one instance
"MailItem|42"     one dot per mail
"QuestItem|3|17"  two keys, in the order the rule declares them
```

**Every dot answers its own question.** There is no aggregation anywhere. The Mail button
has a rule; so does each mail row; they are unrelated, and the button is correct before a
single row exists. That is the trade: a little duplication of intent between a parent
question and its children, in exchange for every dot being independently correct,
independently testable, and cheap to reason about.

**Two lifecycles carry the cost model.**

| | Global (`keys = nil`) | Keyed (`keys = {...}`) |
| --- | --- | --- |
| Created | at boot, by `CreateGlobalRedDots` | on the first `Subscribe` |
| Destroyed | never | with the last unsubscribe |
| Cost | a handful, for the session | one per row, while the screen is open |

A mail list of 500 rows costs 500 dots while it is open and none afterwards.
`ClearSubscriptions()` releases everything at a state-change boundary, so a screen torn
down without unbinding cannot keep dots alive for the rest of the session.

**Events queue; they never compute.** An event marks the live dots of the affected types
pending. One drain per frame computes each pending dot at most once, compares with the
cached value, and notifies only on a change. Fifty events in a frame cost one evaluation
per dot.

**Subscribe is the deliberate exception.** It computes synchronously and pushes the value
before returning, which is what makes a re-entered screen correct on the frame it opens
rather than after the next event. There is a test named after that repro.

### Seen state is a token, not a boolean

`MarkSeen` stores the token the rule reports *now*. A dot is unseen exactly when its
stored token differs from the current one, so new content re-arms a badge with no extra
bookkeeping in the rule — and a nil token means "the data has not loaded", which keeps
the dot off rather than guessing.

Persistence is one PlayerPrefs key holding JSON with a `SAVE_VERSION`. An older or
corrupt blob is discarded with a warning rather than migrated: seen state is cosmetic,
and a wrong guess shows the player badges they already dismissed. Entries are stored as
an **array of `[key, token]` pairs** rather than an object — a set keyed by id would come
back from JSON with string keys, and a set whose key type changed on the round trip is
the kind of bug that only shows up on a device.

The blob is written at most once per frame, from the tick.

### What survives a restart

The demo persists to two namespaced PlayerPrefs keys, and
`DemoMain.ClearSavedState()` wipes both:

| Key | Holds |
| --- | --- |
| `reddot.demo.seen` | the seen blob: one versioned JSON entry per marked dot |
| `reddot.demo.clock` | the demo's game time |

The clock is saved because it has to be. A seen token records *what* the player saw —
the shop's is `day:19675` — so a clock that started again from the epoch on every run
would rewind game time and make every date-derived token look like new content. The
persistence would look broken when the clock was the thing that was wrong. Game time is
restored on boot and never travels backwards: an unparseable or too-early value is
discarded rather than trusted.

What does **not** persist is the fake game data. The mail list, the quest board and the
shop stock are in-memory and start again every session, because they are stand-ins for a
game, not a game. That makes the two halves of a rule visible separately on a restart:

| Dot | After a stop-Play / start-Play |
| --- | --- |
| `Shop` | **stays off** if it was seen and the day has not changed — this is the one to watch |
| `Mail` | **lights again**, correctly: the seen half is remembered, but two fresh unclaimed mails are seeded, and its rule is "unclaimed mail exists OR unseen" |
| `Quests` | off, until something is completed — its token is nil on an empty board |
| `LimitedOffer` | gone, until the patch is applied again |

To watch the shop light up once more, press **Advance time +1 day**: a new day is a new
token, which is exactly the mechanism the persistence is preserving.

### Every action, and the action that undoes it

A rule that reads real game state can only go off when something changes that state. A
dot whose condition can become true with nothing in the demo able to make it false again
lights once and stays lit for the session — a bug that is invisible in review and obvious
in a play-test. Two of these shipped and were fixed; the table is the sweep that closed
the class.

| What lights it | Dots affected | What clears it |
| --- | --- | --- |
| **Add mail** (`Receive`) | `MailItem\|id`, `Mail` | tap the mail row (`Open`), or **Claim all** (`ClaimAll`) |
| boot-seeded mail | `MailItem\|1`, `Mail` | as above |
| **Complete the daily** (`Complete(1,1)`) | `QuestItem\|1\|1`, `Quests` | **Claim the daily**, or tap the **Daily** tab (`Claim(1,1)`) |
| **Unlock the achievement** (`Complete(2,7)`) | `QuestItem\|2\|7`, `Quests` | tap the **Achievements** tab (`Claim(2,7)`) |
| **A free deal arrives** (`AddFreeDeal`) | `Shop` | tap the **Daily deals** tab (`Purchase`) |
| **Start limited offer** | `LimitedOffer`, `Shop` | opening the Shop screen — both types track seen state |
| crossing midnight (**Advance time +1 day**) | `Shop` | opening the Shop screen — the token is the date |

The right-hand column falls into two kinds, and which one a dot uses is the whole of the
`tracksSeen` decision. A dot that tracks seen state is cleared by *looking*, and the
screen that shows it marks it — boot validation now reports any `tracksSeen` type no
screen marks. A dot that reads real state is cleared only by *acting*, and there is no
automatic check for that: "this condition can become true and nothing can make it false"
is not decidable in general, which is why the pairs above are written down.

### Scheduled resets

A rule may expose `resetAt()`. The manager keeps **one** soonest-deadline timer read from
the game clock; when it passes, the due types are queued and the deadlines are
recomputed. They are also recomputed after any flush that did work, because fresh data
can move a boundary. Nothing polls: an idle frame is two number comparisons and a dirty
flag.

### Boot validation

`ValidateRules` rejects the four ways a rule is silently wrong — silently, because every
one of them shows up as "the badge is just always off" or "the badge is sometimes stale",
weeks later:

| Problem | Level |
| --- | --- |
| `token` without `tracksSeen` — the token is never read | error |
| neither `condition` nor `tracksSeen` — it can only ever be false | error |
| an event name that is not in `RedDotEvent` or declared by the patch | error |
| neither `events` nor `resetAt` — it never refreshes by itself | warning |

The walk over each rule's `events` reads every numeric key rather than using `#`, so a
nil hole in the middle of a list cannot swallow the entries after it.

### The reconcile checker

`SetReconcileEnabled(true)` recomputes every live dot once a second and logs `MISMATCH`
for any whose cached value disagrees. **It fixes nothing, on purpose.** A mismatch is not
a glitch to paper over — the cache is right about what it was told, and what it was told
was incomplete. It means a rule is missing an event, and the only correct fix is in the
rule.

### What survived

The FairyGUI badge contract (`redDot` / `state` / `count`), the authored UI package, the
screens, the vendored dependencies, the loader with its patch-shadowing roots, the event
bus, and the hot-update seam. `ReloadRules` still diffs subscriptions; it now also
creates dots for any global type the patch introduces.

The badge's `count` page is intact but currently unused: the engine reports a boolean, so
`RedDotView` always selects `dot`. `RedDotView.Apply(bool, int)` still supports counts, so
a rule that grows a number later needs no change in the view.


### What the example patch does, and why it rewrites a rule

The patch adds the `LimitedOffer` type — and rewrites the shipped `Shop` rule.

The second half is the interesting one. Under v1's parent/child model the lobby Shop
button lit up for a new child by aggregation: for free, and without anyone deciding it
should. v2 has no aggregation, so if the Shop button is to react to the offer, the Shop
*rule* has to say so. A patch owns the whole rule table, not just its new entries, so it
can. Adding a badge is easy in any model; changing what a badge that already shipped
means, on a Tuesday afternoon, is the thing worth being able to do.

Concretely the patch gives `Shop` the new event and folds the offer into its content
stamp:

```lua
token = function()
    local day = Game.Shop:ResetToken()
    local offer = Game:Counter("shop.limitedOffer")
    if offer <= 0 then return day end
    return day .. "|offer:" .. offer
end
```

With no offer running the stamp is byte-for-byte the shipped one, so **installing the
patch on a shop the player has already seen changes nothing visible**. A patch that lights
a badge merely by being installed is a patch nobody trusts.

The behaviour that follows, and that the tests pin down:

| Step | Shop button on Main |
| --- | --- |
| apply the patch on a seen shop | stays off |
| **Start limited offer** | **lights**, and stays lit while the player is elsewhere |
| open the Shop screen | clears — opening counts as seeing the offer, and the Limited Offer badge clears with it |
| **Start limited offer** again | lights again: a new offer is a new stamp |
| in a session restored from a save | identical — a seen mark written before the offer existed cannot match a stamp that carries it |

Rules are not saved, so a new session starts on the shipped rule table and the patch has
to be applied again. After a restart that followed an offer, the Shop button is lit once:
the stored stamp says the player last saw a shop that had an offer in it, and this one
does not. Opening the shop clears it. That is the same "seen state persists, fake data
does not" property as the mailbox, and it is why the demo prints its dot counts.
## Files

```
Assets/Lua/reddot/
  RedDotType.lua      the type-name dictionary
  RedDotEvent.lua     the known event names; validation checks against this
  RedDotRules.lua     the rule table: keys, condition, tracksSeen, token, resetAt, events
  manager.lua         the engine: registry, lifecycles, queue/drain, resets, validation,
                      reconcile, DumpState
  seen_store.lua      token-per-dot seen state, versioned JSON, one write per frame
  json.lua            a minimal encoder/decoder, sized for the save blob

Assets/Lua/patches/
  rules_patch_example.lua   adds the LimitedOffer type, rule and event

Assets/Scripts/RedDot/
  RedDotBridge.cs     boots xLua; RaiseEvent / Flush / Subscribe / MarkSeen / ReloadRules
  RedDotBinder.cs     Bind(component, type, ...keys), SetRedDotActive, disposal safety
  RedDotView.cs       the badge adapter
  RedDotContext.cs    the Game surface rules may read, plus the generic counter
  LuaScriptLoader.cs  ordered search roots; patch folders shadow base files
  SeenPersistence.cs  ISeenPersistence + in-memory and PlayerPrefs stores

Assets/Scripts/Demo/
  DemoMain.cs         boot, screen flow, the (screen, child, type, keys) table
  DemoUIFactory.cs    the code-built screens: fallback UI and test fixtures
  DemoLogPanel.cs     the on-screen log: scrolling list, text field, or console
  FakeGameServices.cs fake mail / quest / shop managers and the fake clock
  RedDotDriver.cs     one Tick per frame, in LateUpdate
```

## Test results

**95 / 95 EditMode**, 64 s, and **12 / 12 PlayMode**, 17 s. Both run headless, and the
PlayMode set runs against the authored UI package. The recorded run is against a clean
project with no Library, after the template cleanup.

```
Unity 6000.0.59f2, NUnit 3.5.0
EditMode  total="95" passed="95" failed="0" inconclusive="0" skipped="0"
PlayMode  total="12" passed="12" failed="0" inconclusive="0" skipped="0"
```

### The engine (56 cases)

| Area | Cases |
| --- | --- |
| Registry keys | type alone, one key, two keys in order, and integers that must not pick up a decimal point |
| Lifecycles | globals exist from boot with nobody watching, a global is correct before its screen was ever opened, keyed dots are created on first subscribe and destroyed with the last, globals survive their last subscriber, `ClearSubscriptions` keeps globals and drops the rest, double-subscribe is counted honestly |
| Immediate compute | `Subscribe` pushes before returning, **the re-enter-screen repro**, and binding a type no rule defines reads false and says so once |
| Queue and drain | events compute nothing, 50 events cost one evaluation per dot, unchanged dots do not notify, an idle flush does no work at all, unsubscribed events never reach Lua, the bus carries exactly the named events, a destroyed keyed dot costs nothing |
| Seen and tokens | a tracked dot starts on and clears when marked, new content moves the token and it returns, a nil token keeps it off, types that track real state ignore `MarkSeen`, marking with no content stores nothing |
| Persistence | round trip through one blob, an older version is discarded, a corrupt blob is discarded, three marks cost one write |
| Scheduled resets | the soonest deadline is the one kept, crossing it requeues and reschedules with no event at all, a deadline that has not arrived costs nothing |
| Safety | a throwing rule reads false while the rest keeps working, and complains once rather than every frame |
| Validation | the four error cases, a patch declaring its own events, the shipped rules validating clean, and a nil hole not swallowing a later typo |
| Reconcile | flags a rule missing an event, is quiet when the rules are complete, and sweeps on its own timer |
| Hot update | the patch adds a type the build never knew about, a binding made before the reload picks it up, a retired rule unsubscribes on the C# bus, a reload reports only what moved |
| Diagnostics | `DumpState` renders the registry, the seen set and the stats |
| Fuzz | 10 000 random events, marks, subscribes, unsubscribes and reloads |

The fuzz run: 10 000 iterations → 6 122 events reaching Lua, 1 667 flushes, 15 hot
reloads, 6 711 rule evaluations for 7 579 queue entries. After **every** flush and
**every** reload it asserts two things: `Reconcile()` returns zero — every cached value
equals a fresh recomputation — and the set of notified keys is exactly the set whose
value differs, with nothing notified twice.

### The view layer (29 cases)

| Area | Cases |
| --- | --- |
| Page selection | hidden really removes the artwork through the gear, a boolean value selects `dot`, count 1 stays a dot, counts above one select `count`, 99 / 100 / 41235 → `99` / `99+` / `99+`, a badge cycling all three, and the badge is not touchable |
| Degrading | no `count` field falls back to the dot, no `state` controller falls back to visibility, no `redDot` child is inert, a disposed host is not touched |
| Binding | the current value is pushed on bind, a bound badge follows the engine, unbind is idempotent, `UnbindAll` releases one screen and its keyed dots |
| Pooled reuse | **rebinding a recycled row drops the old binding and destroys the dot it was holding open**, and rebinding to the same dot does not stack subscriptions |
| Disposal | a disposed component releases itself on the next update, and can be swept explicitly |
| Kill switch | an inactive badge stays hidden while the rule says yes, reactivating shows the *current* answer rather than the old one, an inactive binding still tracks underneath, and setting it on something unbound is a no-op |
| Hot update | a badge bound to a type with no rule lights up when the patch introduces it |

### The demo scene (4 PlayMode cases)

The scene loads, the FairyGUI root comes up, and the Mail button is bound to the global
`Mail` dot. Mail is claimed and the badge does **not** move — events only queue — and by
the next frame the driver has ticked and it is off. Opening the mail screen creates keyed
dots and leaving destroys them, back to exactly the count from before. The example patch
adds `LimitedOffer` and the offer button lights it. Advancing the clock a day fires the
scheduled reset with no event raised at all.

### Running the tests

```
"C:\Program Files\Unity\Hub\Editor\6000.0.59f2\Editor\Unity.exe" ^
  -batchmode -nographics ^
  -projectPath C:\SampleProjects\ui-reddot-system ^
  -runTests -testPlatform EditMode ^
  -testResults C:\SampleProjects\ui-reddot-system\TestResults\results.xml ^
  -logFile -
```

Swap `EditMode` for `PlayMode` for the scene tests. Or use
**Window → General → Test Runner**.

> **Note on the recorded runs.** Unity refuses batchmode on a project another Editor
> instance has open. The recorded runs executed against a byte-identical copy of
> `Assets/`, `Packages/` and `ProjectSettings/` in a scratch directory. **Close the
> Editor** and the command above runs against the repository directly.

## What the play-tests found

Five findings came out of play-testing, and between them they shaped the architecture
more than any amount of up-front design did. The first killed the original model: badges
were blank when a screen was re-entered, because a parent's value was a function of
children that were rebuilt on the way in — which is the same defect as a lobby button
that cannot be right until the screen behind it has been opened, and it is why v2 has no
aggregation and why `Subscribe` computes synchronously. The second was a mail added while
the inbox was open that would not clear: the screen marked the inbox seen once, when it
opened, so a mail arriving afterwards moved the token past the mark and the badge stuck —
a screen that is on screen is being looked at, and now re-marks after every action. The
third and fourth were the same bug twice, an achievement and then a free deal that lit and
could never be cleared, because nothing in the demo claimed either; that pair is what
turned a one-off fix into the action-pair table above and into the boot check for
`tracksSeen` types no screen marks. The fifth was the limited offer failing to light the
lobby Shop button — a behaviour v1 got free from aggregation, lost when aggregation was
deleted, and missed by a test that had been weakened to `Is.Not.Null` in the same commit
that broke it. Deleting a mechanism means finding everything it was quietly providing, and
a weakened assertion is worse than a deleted one, because it still looks like coverage.

A sixth thing surfaced while verifying persistence: the demo had never used PlayerPrefs at
all. It does now, and the game clock is saved with the seen blob, because a token that
encodes the date is meaningless if time restarts.

## What is deliberately not here

The project is complete as a demonstration of the red dot system, and these were scoped
out rather than forgotten:

- **An architecture diagram in the README.** The three GIFs are in `docs/media/`; a
  drawn diagram of the event -> queue -> drain -> notify path would still help.
- **A CI workflow.** The EditMode suite runs headless in one command, but a GitHub Actions
  job needs a Unity licence secret, which a public sample repository should not carry.
- **Shipping concerns.** On a device the Lua would come from StreamingAssets or an
  AssetBundle rather than `Assets/Lua`, the UI package likewise, and IL2CPP would need
  xLua's code generation step. None of that changes the engine; all of it changes where
  bytes come from.
- **A "revert patch" button**, so the hot-update demo can be shown twice without
  restarting. `ReloadRulesFromModule` already does it; only the button is missing.

## Notes

- **Close the Unity Editor** before running the batchmode test command, or it will refuse
  the project lock. The recorded runs executed against a byte-identical copy of `Assets/`,
  `Packages/` and `ProjectSettings/` in a scratch directory.
- Two assembly definitions were added inside the vendored xLua tree (`XLua`,
  `XLua.Editor`), recorded in `Assets/XLua/VENDORED.md`. Unavoidable: an asmdef assembly
  cannot reference the default `Assembly-CSharp`.
- `DemoMain.ClearSavedState()` wipes the demo's two PlayerPrefs keys if you want the
  badges back from a clean slate.
