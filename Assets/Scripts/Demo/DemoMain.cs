using System;
using System.Collections.Generic;
using System.IO;
using FairyGUI;
using RedDot.Events;
using UnityEngine;

namespace RedDot.Demo
{
    /// <summary>
    /// The demo: a fake game whose screens are wired to the red dot engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything specific to a badge lives in Lua. This class knows how to boot
    /// FairyGUI, which child of which screen watches which dot, and which button pokes
    /// which fake service. It never asks what a badge means.
    /// </para>
    /// <para>
    /// The two lifecycles are both on screen. The Mail / Quests / Shop buttons watch
    /// global dots that exist from boot, so they are correct before their screens have
    /// ever been opened. The mail rows watch keyed dots that are created when a row
    /// binds and destroyed when the list is torn down — the debug log prints the live
    /// dot count on the way in and out, which is the whole point of the exercise.
    /// </para>
    /// </remarks>
    public sealed class DemoMain : MonoBehaviour
    {
        #region Wiring tables

        /// <summary>UI package name, as authored in the FairyGUI Editor.</summary>
        public const string PackageName = "RedDotDemo";

        /// <summary>
        /// Where the package is looked for, in order. The first entry is the authoring
        /// output folder and works in the Editor; the others are the Resources convention
        /// a player build needs.
        /// </summary>
        private static readonly string[] PackageCandidates =
        {
            "Assets/FairyGUI-Packages/" + PackageName,
            PackageName,
            "UI/" + PackageName,
        };

        private const string MainScreen = "Main";
        private const string MailScreen = "MailScreen";
        private const string QuestsScreen = "QuestsScreen";
        private const string ShopScreen = "ShopScreen";

        // Type names, mirrored from Assets/Lua/reddot/RedDotType.lua. These five strings
        // and the two below are the whole of what C# knows about the badge model.
        private const string TypeMail = "Mail";
        private const string TypeQuests = "Quests";
        private const string TypeShop = "Shop";
        private const string TypeMailItem = "MailItem";
        private const string TypeQuestItem = "QuestItem";

        /// <summary>A type only the example patch defines. Bound before it exists.</summary>
        private const string TypeLimitedOffer = "LimitedOffer";

        /// <summary>The two demo quests, so the two-key case is visible on screen.</summary>
        private static readonly (int Chapter, int Quest) DailyQuest = (1, 1);
        private static readonly (int Chapter, int Quest) AchievementQuest = (2, 7);

        /// <summary>A fixed mail id the Mail screen shows outside the list.</summary>
        private const int SystemMailId = 1;

        /// <summary>
        /// Which child of which screen watches which dot. Keys are supplied per row.
        /// Adding a badge is a row here; adding a *rule* needs no C# at all.
        /// </summary>
        private static readonly Dictionary<string, (string Child, string Type, object[] Keys)[]> Badges =
            new Dictionary<string, (string, string, object[])[]>
            {
                [MainScreen] = new[]
                {
                    ("btnMail", TypeMail, new object[0]),
                    ("btnQuests", TypeQuests, new object[0]),
                    ("btnShop", TypeShop, new object[0]),
                },
                [MailScreen] = new[]
                {
                    // The same global dot the main screen shows: one dot, two subscribers.
                    ("btnInbox", TypeMail, new object[0]),
                    ("btnSystem", TypeMailItem, new object[] { SystemMailId }),
                },
                [QuestsScreen] = new[]
                {
                    ("btnDaily", TypeQuestItem, new object[] { DailyQuest.Chapter, DailyQuest.Quest }),
                    ("btnAchievements", TypeQuestItem,
                        new object[] { AchievementQuest.Chapter, AchievementQuest.Quest }),
                },
                [ShopScreen] = new[]
                {
                    ("btnDailyDeals", TypeShop, new object[0]),

                    // No rule defines this until the example patch is applied. Binding a
                    // type that does not exist yet is legal and reads as off, so the view
                    // is simply waiting for content that has not shipped.
                    ("btnLimitedOffer", TypeLimitedOffer, new object[0]),
                },
            };

        /// <summary>
        /// What each screen marks seen -- when it opens, and again after anything it
        /// shows changes while it is open. Only the types that track seen state respond;
        /// the rest ignore it, which is why a screen can mark everything it shows without
        /// knowing which is which.
        /// </summary>
        private static readonly Dictionary<string, string[]> SeenTypesByScreen =
            new Dictionary<string, string[]>
            {
                [MailScreen] = new[] { TypeMail },
                [QuestsScreen] = new[] { TypeQuests },
                [ShopScreen] = new[] { TypeShop, TypeLimitedOffer },
            };

        private const string MailListChild = "listMail";
        private const string MailItemComponent = "MailListItem";
        private const string MailListOwner = "MailScreen.list";

        private const string PatchFile = "Lua/patches/rules_patch_example.lua";
        private const string LimitedOfferEvent = "LimitedOfferStarted";
        private const string LimitedOfferCounter = "shop.limitedOffer";

        #endregion

        #region State

        private EventBus _bus;
        private FakeClock _clock;
        private FakeMailService _mail;
        private FakeQuestService _quests;
        private FakeShopService _shop;
        private RedDotContext _context;
        private RedDotBridge _bridge;
        private RedDotBinder _binder;
        private RedDotDriver _driver;

        private readonly Dictionary<string, GComponent> _screens = new Dictionary<string, GComponent>(StringComparer.Ordinal);
        private readonly List<GObject> _mailRows = new List<GObject>();

        private GComponent _current;
        private DemoLogPanel _logPanel;
        private GList _mailList;
        private bool _reconcileOn;

        /// <summary>True when the authored package was not found and code-built screens are in use.</summary>
        public bool UsingFallbackUI { get; private set; }

        public RedDotBridge Bridge => _bridge;

        public RedDotBinder Binder => _binder;

        public FakeClock Clock => _clock;

        public FakeMailService Mail => _mail;

        public FakeQuestService Quests => _quests;

        public FakeShopService Shop => _shop;

        /// <summary>Where the demo writes its running commentary.</summary>
        public DemoLogPanel LogPanel => _logPanel;

        /// <summary>The screen currently on the root, by component name.</summary>
        public string CurrentScreen { get; private set; }

        /// <summary>One of the built screens by component name, or null.</summary>
        public GComponent GetScreen(string name)
        {
            return _screens.TryGetValue(name, out var screen) ? screen : null;
        }

        /// <summary>The bound mail rows currently in the list. Empty off the mail screen.</summary>
        public IReadOnlyList<GObject> MailRows => _mailRows;

        #endregion

        #region Lifetime

        private void Start()
        {
            Boot();
        }

        /// <summary>Split out of <see cref="Start"/> so a test can drive it directly.</summary>
        public void Boot()
        {
            if (_bridge != null)
            {
                return;
            }

            FairyGuiEnvironment.EnsureDefaultFont();

            _bus = new EventBus();
            _clock = new FakeClock();
            _mail = new FakeMailService(_bus);
            _quests = new FakeQuestService(_bus);
            _shop = new FakeShopService(_bus, _clock);
            _context = new RedDotContext(_mail, _quests, _shop, _clock);

            _bridge = new RedDotBridge(new RedDotBridgeOptions
            {
                Bus = _bus,
                Context = _context,

                // Deliberately not PlayerPrefs: every run of the demo should start with
                // nothing seen, so the badges are actually visible.
                SeenPersistence = new InMemorySeenPersistence(),
                Log = message => Debug.Log("[RedDot] " + message),
            });

            _binder = new RedDotBinder(_bridge);

            _driver = gameObject.GetComponent<RedDotDriver>();
            if (_driver == null)
            {
                _driver = gameObject.AddComponent<RedDotDriver>();
            }

            _driver.Attach(_bridge, _clock);

            UsingFallbackUI = !TryLoadPackage();
            if (UsingFallbackUI)
            {
                Debug.Log(
                    "[RedDotDemo] UI package '" + PackageName + "' not found -- using fallback UI. " +
                    "Author the package per docs/PACKAGE_SPEC.md and export it to " +
                    "Assets/FairyGUI-Packages/ to see the real thing.");
            }

            GRoot.inst.SetContentScaleFactor(DemoUIFactory.DesignWidth, DemoUIFactory.DesignHeight);

            // Some mail so the first frame has something to show.
            _mail.Receive("Welcome");
            _mail.Receive("Season rewards");

            BuildScreens();
            Show(MainScreen);

            AuditSeenCoverage();

            Log(UsingFallbackUI ? "fallback UI (see docs/PACKAGE_SPEC.md)" : "UI package loaded");
            Log(_bridge.Counts().Total + " dots live at boot");
        }

        private void OnDestroy()
        {
            Teardown();
        }

        public void Teardown()
        {
            _driver?.Detach();

            ClearMailList();

            foreach (var screen in _screens.Values)
            {
                screen.RemoveFromParent();
                screen.Dispose();
            }

            _screens.Clear();
            _current = null;
            _logPanel = null;
            _mailList = null;

            _bridge?.Dispose();
            _bridge = null;
            _binder = null;
        }

        #endregion

        #region Package loading

        private bool TryLoadPackage()
        {
            if (UIPackage.GetByName(PackageName) != null)
            {
                return true;
            }

            foreach (var candidate in PackageCandidates)
            {
                if (!PackageExists(candidate))
                {
                    continue;
                }

                try
                {
                    if (UIPackage.AddPackage(candidate) != null)
                    {
                        Debug.Log("[RedDotDemo] loaded UI package from '" + candidate + "'");
                        return true;
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[RedDotDemo] failed to load '" + candidate + "': " + exception.Message);
                }
            }

            return false;
        }

        /// <summary>
        /// FairyGUI throws rather than returning null when a descriptor is missing, so the
        /// existence check has to happen first.
        /// </summary>
        private static bool PackageExists(string candidate)
        {
            if (candidate.StartsWith("Assets/", StringComparison.Ordinal))
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
                return projectRoot != null && File.Exists(Path.Combine(projectRoot, candidate + "_fui.bytes"));
            }

            return Resources.Load<TextAsset>(candidate + "_fui") != null;
        }

        #endregion

        #region Screens

        private void BuildScreens()
        {
            BuildMain();
            BuildMail();
            BuildQuests();
            BuildShop();
        }

        private GComponent CreateScreen(string componentName, string heading)
        {
            if (!UsingFallbackUI)
            {
                var fromPackage = UIPackage.CreateObject(PackageName, componentName)?.asCom;
                if (fromPackage != null)
                {
                    return fromPackage;
                }

                Debug.LogWarning(
                    "[RedDotDemo] package '" + PackageName + "' has no component '" + componentName +
                    "'; falling back to the code-built screen. Check docs/PACKAGE_SPEC.md.");
            }

            return DemoUIFactory.CreateScreen(componentName, heading);
        }

        private void BuildMain()
        {
            var screen = CreateScreen(MainScreen, "Red dot demo");

            if (UsingFallbackUI)
            {
                var column = new DemoUIFactory.Column(screen, 130);
                column.AddRow(new List<GObject>
                {
                    DemoUIFactory.CreateTabButton("btnMail", "Mail"),
                    DemoUIFactory.CreateTabButton("btnQuests", "Quests"),
                    DemoUIFactory.CreateTabButton("btnShop", "Shop"),
                });
                column.Add(DemoUIFactory.CreateActionButton("btnApplyPatch", "Apply Lua patch"), 10);
                column.Add(DemoUIFactory.CreateActionButton("btnStartOffer", "Start limited offer"), 10);
                column.Add(DemoUIFactory.CreateActionButton("btnAdvanceDay", "Advance time +1 day"), 10);
                column.Add(DemoUIFactory.CreateActionButton("btnReconcile", "Reconcile: off"), 10);
                column.Add(DemoUIFactory.CreateActionButton("btnDumpTree", "Dump state"), 10);
                column.Add(DemoUIFactory.CreateOutputPanel("txtDebug", DemoUIFactory.ButtonWidth, 380));
            }

            _logPanel = new DemoLogPanel(screen, PackageName, message => Debug.Log("[RedDotDemo] " + message));

            OnClick(screen, "btnMail", () => Show(MailScreen));
            OnClick(screen, "btnQuests", () => Show(QuestsScreen));
            OnClick(screen, "btnShop", () => Show(ShopScreen));

            OnClick(screen, "btnApplyPatch", ApplyPatch);
            OnClick(screen, "btnStartOffer", StartLimitedOffer);
            OnClick(screen, "btnAdvanceDay", AdvanceDay);
            OnClick(screen, "btnReconcile", ToggleReconcile);
            OnClick(screen, "btnDumpTree", () => Debug.Log(_bridge.DumpState()));

            Register(MainScreen, screen);
        }

        private void BuildMail()
        {
            var screen = CreateScreen(MailScreen, "Mail");

            if (UsingFallbackUI)
            {
                var column = new DemoUIFactory.Column(screen, 130);
                column.AddRow(new List<GObject>
                {
                    DemoUIFactory.CreateTabButton("btnInbox", "Inbox"),
                    DemoUIFactory.CreateTabButton("btnSystem", "System"),
                });
                column.Add(DemoUIFactory.CreateMailList(MailListChild, 460), 10);
                column.Add(DemoUIFactory.CreateActionButton("btnAddMail", "Add mail"), 10);
                column.Add(DemoUIFactory.CreateActionButton("btnClaimAll", "Claim all"), 10);
                column.Add(DemoUIFactory.CreateActionButton("btnBack", "Back"), 10);
            }

            OnAction(screen, "btnAddMail", () =>
            {
                var id = _mail.Receive();
                Log("mail " + id + " received");
                PopulateMailList();
            });

            OnAction(screen, "btnClaimAll", () =>
            {
                Log(_mail.ClaimAll() + " mail claimed");
                PopulateMailList();
            });

            Register(MailScreen, screen);
        }

        private void BuildQuests()
        {
            var screen = CreateScreen(QuestsScreen, "Quests");

            if (UsingFallbackUI)
            {
                var column = new DemoUIFactory.Column(screen, 130);
                column.AddRow(new List<GObject>
                {
                    DemoUIFactory.CreateTabButton("btnDaily", "Daily"),
                    DemoUIFactory.CreateTabButton("btnAchievements", "Achievements"),
                });
                column.Add(DemoUIFactory.CreateActionButton("btnCompleteQuest", "Complete the daily"), 10);
                column.Add(DemoUIFactory.CreateActionButton("btnClaimQuest", "Claim the daily"), 10);
                column.Add(DemoUIFactory.CreateActionButton("btnUnlockAchievement", "Unlock the achievement"), 10);
                column.Add(DemoUIFactory.CreateActionButton("btnBack", "Back"), 10);
            }

            OnAction(screen, "btnCompleteQuest", () =>
            {
                _quests.Complete(DailyQuest.Chapter, DailyQuest.Quest);
                Log("daily quest is claimable");
            });

            OnAction(screen, "btnClaimQuest", () =>
            {
                _quests.Claim(DailyQuest.Chapter, DailyQuest.Quest);
                Log("daily quest claimed");
            });

            OnAction(screen, "btnUnlockAchievement", () =>
            {
                _quests.Complete(AchievementQuest.Chapter, AchievementQuest.Quest);
                Log("achievement is claimable");
            });

            // Tapping a quest claims it, the way tapping a mail row reads it. A dot whose
            // condition is "this is claimable" can only go off when something claims it,
            // so every quest on screen needs an action that does.
            OnAction(screen, "btnDaily", () => ClaimQuest(DailyQuest, "daily quest"));
            OnAction(screen, "btnAchievements", () => ClaimQuest(AchievementQuest, "achievement"));

            Register(QuestsScreen, screen);
        }

        private void ClaimQuest((int Chapter, int Quest) quest, string label)
        {
            if (!_quests.IsClaimable(quest.Chapter, quest.Quest))
            {
                return;
            }

            _quests.Claim(quest.Chapter, quest.Quest);
            Log(label + " claimed");
        }

        private void BuildShop()
        {
            var screen = CreateScreen(ShopScreen, "Shop");

            if (UsingFallbackUI)
            {
                var column = new DemoUIFactory.Column(screen, 130);
                column.AddRow(new List<GObject>
                {
                    DemoUIFactory.CreateTabButton("btnDailyDeals", "Daily deals"),
                    DemoUIFactory.CreateTabButton("btnLimitedOffer", "Limited offer"),
                });
                column.Add(DemoUIFactory.CreateActionButton("btnNewDeal", "A free deal arrives"), 10);
                column.Add(DemoUIFactory.CreateActionButton("btnBack", "Back"), 10);
            }

            OnAction(screen, "btnNewDeal", () =>
            {
                _shop.AddFreeDeal();
                Log("free deal added");
            });

            Register(ShopScreen, screen);
        }

        /// <summary>
        /// Binds a screen's badges and its back button, then files it away.
        /// </summary>
        private void Register(string name, GComponent screen)
        {
            _screens[name] = screen;

            if (name == MailScreen)
            {
                _mailList = screen.GetChild(MailListChild) as GList;
                if (_mailList == null)
                {
                    Debug.LogWarning(
                        "[RedDotDemo] MailScreen has no '" + MailListChild +
                        "' list; the keyed-dot demo will be missing. Check docs/PACKAGE_SPEC.md.");
                }
            }

            if (name != MainScreen)
            {
                OnClick(screen, "btnBack", () => Show(MainScreen));
            }
        }

        /// <summary>
        /// Binds a screen's badges. Called when the screen goes up, not when it is built.
        /// </summary>
        /// <remarks>
        /// That distinction is the keyed lifecycle. Binding everything at boot would keep
        /// a dot alive for every row of every screen for the whole session, which is
        /// exactly what this model exists to avoid — the dots a screen needs come into
        /// being when it opens and go away with it.
        /// </remarks>
        private void BindScreenBadges(string name, GComponent screen)
        {
            if (!Badges.TryGetValue(name, out var badges))
            {
                return;
            }

            foreach (var badge in badges)
            {
                if (screen.GetChild(badge.Child) is GComponent host)
                {
                    _binder.BindOwned(host, name, badge.Type, badge.Keys);
                }
                else
                {
                    Debug.LogWarning(
                        "[RedDotDemo] screen '" + name + "' has no child '" + badge.Child +
                        "'; that badge will not update. Check docs/PACKAGE_SPEC.md.");
                }
            }
        }

        private void Show(string name)
        {
            if (!_screens.TryGetValue(name, out var screen) || CurrentScreen == name)
            {
                return;
            }

            var leavingMail = CurrentScreen == MailScreen;
            var before = _bridge.Counts();

            // Leaving a screen releases everything it bound. That is what destroys its
            // keyed dots, and the counts on either side are the demo's whole argument for
            // the keyed lifecycle.
            if (CurrentScreen != null)
            {
                ClearMailList();
                _binder.UnbindAll(CurrentScreen);
            }

            _current?.RemoveFromParent();

            GRoot.inst.AddChild(screen);
            _current = screen;
            CurrentScreen = name;

            BindScreenBadges(name, screen);

            if (name == MailScreen)
            {
                PopulateMailList();
            }

            var after = _bridge.Counts();
            if (leavingMail || name == MailScreen)
            {
                Log((leavingMail ? "left mail: " : "entered mail: ") +
                    before.Total + " dots (" + before.Keyed + " keyed) -> " +
                    after.Total + " (" + after.Keyed + " keyed)");
            }

            // Opening a section is what "the player has looked at this" means.
            MarkCurrentScreenSeen();
        }

        #endregion

        #region The mail list

        /// <summary>
        /// Rebuilds the rows and binds one keyed dot per mail. Every row is its own dot:
        /// there is no aggregate anywhere, and the Mail button above is answering its own
        /// separate question about the mailbox.
        /// </summary>
        private void PopulateMailList()
        {
            if (_mailList == null)
            {
                return;
            }

            ClearMailList();

            foreach (var mail in _mail.Mails)
            {
                var row = CreateMailRow();
                if (row == null)
                {
                    return;
                }

                _mailList.AddChild(row);
                _mailRows.Add(row);

                if (row.asCom?.GetChild("title") is GTextField title)
                {
                    title.text = "#" + mail.Id + "  " + mail.Subject;
                }
                else
                {
                    row.text = "#" + mail.Id + "  " + mail.Subject;
                }

                if (row.asCom != null)
                {
                    _binder.BindOwned(row.asCom, MailListOwner, TypeMailItem, mail.Id);
                }

                var mailId = mail.Id;
                row.onClick.Add(() =>
                {
                    if (!_mail.Open(mailId))
                    {
                        return;
                    }

                    Log("opened mail " + mailId);
                    MarkCurrentScreenSeen();
                });
            }

            _mailList.EnsureBoundsCorrect();
        }

        private GObject CreateMailRow()
        {
            if (!UsingFallbackUI)
            {
                var fromPackage = UIPackage.CreateObject(PackageName, MailItemComponent);
                if (fromPackage != null)
                {
                    return fromPackage;
                }

                Debug.LogWarning(
                    "[RedDotDemo] package '" + PackageName + "' has no component '" + MailItemComponent +
                    "'; using the code-built row. Check docs/PACKAGE_SPEC.md.");
            }

            return DemoUIFactory.CreateMailListItem();
        }

        /// <summary>
        /// Unbinds every row and disposes it. Unbinding is what destroys the keyed dots;
        /// disposing without it would work too, because a disposed component releases
        /// itself, but doing it explicitly keeps the counts honest at the moment of the
        /// screen change rather than at the next update.
        /// </summary>
        private void ClearMailList()
        {
            if (_binder != null)
            {
                _binder.UnbindAll(MailListOwner);
            }

            // Empty the list itself rather than only the rows this class put in it. A list
            // authored in the FairyGUI Editor carries a design-time placeholder item, and
            // that placeholder is a child like any other: left alone it sits at the top of
            // the mailbox for the whole session, showing mock-up text and a badge nothing
            // is bound to. The debug log panel clears its own list the same way, on boot.
            if (_mailList != null)
            {
                while (_mailList.numChildren > 0)
                {
                    _mailList.RemoveChildAt(0, true);
                }
            }

            // Rows are children of the list, so the loop above has already disposed them;
            // this is the fallback for a list that could not be found at all.
            foreach (var row in _mailRows)
            {
                if (row.isDisposed)
                {
                    continue;
                }

                row.RemoveFromParent();
                row.Dispose();
            }

            _mailRows.Clear();
        }

        #endregion

        #region Debug actions

        private void ApplyPatch()
        {
            var path = Path.Combine(Application.dataPath, PatchFile);
            if (!File.Exists(path))
            {
                Log("patch file missing: " + PatchFile);
                return;
            }

            try
            {
                var changed = _bridge.ReloadRules(File.ReadAllText(path));

                // A patch can introduce a type that tracks seen state; if no screen marks
                // it, that is a permanently lit badge.
                AuditSeenCoverage();

                Log("patch applied, " + changed + " dot(s) changed, " + _bridge.Counts().Total + " live");
                Debug.Log("[RedDotDemo] after patch:\n" + _bridge.DumpState());
            }
            catch (Exception exception)
            {
                Log("patch failed: " + exception.Message);
                Debug.LogException(exception);
            }
        }

        /// <summary>
        /// Bumps the generic counter the patch's rule reads and raises the event it
        /// subscribed to. C# knows two strings here and nothing about what the badge does
        /// with them — when it lights up, when it clears, and what re-arms it all live in
        /// the patch.
        /// </summary>
        private void StartLimitedOffer()
        {
            _context.SetCounter(LimitedOfferCounter, _context.Counter(LimitedOfferCounter) + 1);
            _bridge.RaiseEvent(LimitedOfferEvent);
            Log("raised " + LimitedOfferEvent);
        }

        private void AdvanceDay()
        {
            _clock.AdvanceDays(1);
            _quests.RollOverDay();
            Log("advanced to day " + _clock.Day + "; next reset at " + _bridge.NextDeadline());
        }

        private void ToggleReconcile()
        {
            _reconcileOn = !_reconcileOn;
            _bridge.SetReconcileEnabled(_reconcileOn);

            if (GetScreen(MainScreen)?.GetChild("btnReconcile") is GComponent button &&
                button.GetChild("title") is GTextField title)
            {
                title.text = "Reconcile: " + (_reconcileOn ? "on" : "off");
            }

            Log("reconcile " + (_reconcileOn ? "on" : "off") +
                (_reconcileOn ? " (recomputes everything once a second and logs MISMATCH)" : ""));
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Wires a button that changes game state, and re-marks the open screen seen
        /// afterwards.
        /// </summary>
        /// <remarks>
        /// Marking once, on open, is not enough. A seen token records <em>what</em> the
        /// player saw, so mail arriving while they are looking at the inbox moves the
        /// token past the mark and the Mail badge stays lit -- even after every mail has
        /// been read -- until they leave and come back. A screen that is on screen is
        /// being looked at, so anything it shows is seen the moment it changes.
        /// </remarks>
        private void OnAction(GComponent screen, string childName, Action handler)
        {
            OnClick(screen, childName, () =>
            {
                handler();
                MarkCurrentScreenSeen();
            });
        }

        /// <summary>
        /// Types that track seen state but that no screen ever marks.
        /// </summary>
        /// <remarks>
        /// A badge of such a type goes off only when somebody marks it seen, so if no
        /// screen does, it lights once and stays lit for the rest of the session with no
        /// way for the player to clear it. That is invisible in code review and obvious
        /// in a play-test, which is the wrong way round -- hence the boot check.
        /// </remarks>
        public static IReadOnlyList<string> FindUnmarkedSeenTypes(IEnumerable<string> seenTrackingTypes)
        {
            var marked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var types in SeenTypesByScreen.Values)
            {
                foreach (var type in types)
                {
                    marked.Add(type);
                }
            }

            var unmarked = new List<string>();
            foreach (var type in seenTrackingTypes ?? Array.Empty<string>())
            {
                if (!string.IsNullOrEmpty(type) && !marked.Contains(type))
                {
                    unmarked.Add(type);
                }
            }

            return unmarked;
        }

        private void AuditSeenCoverage()
        {
            foreach (var type in FindUnmarkedSeenTypes(_bridge.SeenTrackingTypes()))
            {
                Debug.LogWarning(
                    "[RedDotDemo] type '" + type + "' tracks seen state but no screen marks it seen; " +
                    "its badge would light once and never clear. Add it to SeenTypesByScreen.");
            }
        }

        /// <summary>Re-marks whatever the open screen displays. Types that track real
        /// state rather than seen state ignore it.</summary>
        private void MarkCurrentScreenSeen()
        {
            if (CurrentScreen == null || !SeenTypesByScreen.TryGetValue(CurrentScreen, out var types))
            {
                return;
            }

            foreach (var type in types)
            {
                _bridge.MarkSeen(type);
            }
        }

        private static void OnClick(GComponent screen, string childName, Action handler)
        {
            var child = screen.GetChild(childName);
            if (child == null)
            {
                return;
            }

            child.onClick.Add(() => handler());
        }

        /// <summary>
        /// Appends a line to whichever debug panel the UI package turned out to have —
        /// a scrolling list, a plain text field, or the console.
        /// </summary>
        private void Log(string message)
        {
            _logPanel?.Append(message);
        }

        #endregion
    }
}
