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
    /// Everything specific to a badge lives in Lua. This class only knows how to boot
    /// FairyGUI, which child of which screen shows which node path, and which button
    /// pokes which fake service. Search it for "Inbox" and the only hits are a path
    /// string in a table and a button label.
    /// </para>
    /// <para>
    /// If the authored UI package is missing it builds the same screens in code (see
    /// <see cref="DemoUIFactory"/>) rather than failing, so the flow is playable before
    /// the <c>.fui</c> exists and so the scene can be smoke-tested headlessly.
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

        /// <summary>
        /// Which child of which screen watches which node. This table is the whole of
        /// what C# knows about the badge tree, and adding a row to it is the only reason
        /// this file would ever change.
        /// </summary>
        private static readonly Dictionary<string, (string Child, string Path)[]> Badges =
            new Dictionary<string, (string, string)[]>
            {
                [MainScreen] = new[]
                {
                    ("btnMail", "Main.Mail"),
                    ("btnQuests", "Main.Quests"),
                    ("btnShop", "Main.Shop"),
                },
                [MailScreen] = new[]
                {
                    ("btnInbox", "Main.Mail.Inbox"),
                    ("btnSystem", "Main.Mail.System"),
                },
                [QuestsScreen] = new[]
                {
                    ("btnDaily", "Main.Quests.Daily"),
                    ("btnAchievements", "Main.Quests.Achievements"),
                },
                [ShopScreen] = new[]
                {
                    ("btnDailyDeals", "Main.Shop.DailyDeals"),

                    // Bound to a path that has no rule until the example patch is
                    // applied. Binding an unknown path is legal and reads as hidden, so
                    // the view is simply waiting for content that does not exist yet.
                    ("btnLimitedOffer", "Main.Shop.LimitedOffer"),
                },
            };

        /// <summary>
        /// Tabs that clear their own badge when opened. Only the leaf tabs do this: a
        /// player who opens the Mail screen has not yet read the system notice inside it.
        /// </summary>
        private static readonly HashSet<string> MarkSeenOnClick = new HashSet<string>(StringComparer.Ordinal)
        {
            MailScreen, QuestsScreen, ShopScreen,
        };

        private const string PatchFile = "Lua/patches/rules_patch_example.lua";
        private const string LimitedOfferEvent = "LimitedOfferStarted";

        private const int LogLines = 12;

        #endregion

        #region State

        private EventBus _bus;
        private FakeMailService _mail;
        private FakeQuestService _quests;
        private FakeShopService _shop;
        private RedDotContext _context;
        private RedDotBridge _bridge;
        private RedDotBinder _binder;
        private RedDotDriver _driver;

        private readonly Dictionary<string, GComponent> _screens = new Dictionary<string, GComponent>(StringComparer.Ordinal);
        private readonly List<string> _log = new List<string>();

        private GComponent _current;
        private GTextField _output;

        /// <summary>True when the authored package was not found and code-built screens are in use.</summary>
        public bool UsingFallbackUI { get; private set; }

        public RedDotBridge Bridge => _bridge;

        public RedDotBinder Binder => _binder;

        public FakeMailService Mail => _mail;

        public FakeQuestService Quests => _quests;

        public FakeShopService Shop => _shop;

        /// <summary>One of the built screens by component name, or null.</summary>
        public GComponent GetScreen(string name)
        {
            return _screens.TryGetValue(name, out var screen) ? screen : null;
        }

        /// <summary>The screen currently on the root, by component name.</summary>
        public string CurrentScreen { get; private set; }

        #endregion

        #region Lifetime

        private void Start()
        {
            Boot();
        }

        /// <summary>
        /// Split out of <see cref="Start"/> so a test can drive it without waiting for a
        /// scene load.
        /// </summary>
        public void Boot()
        {
            if (_bridge != null)
            {
                return;
            }

            FairyGuiEnvironment.EnsureDefaultFont();

            _bus = new EventBus();
            _mail = new FakeMailService(_bus);
            _quests = new FakeQuestService(_bus);
            _shop = new FakeShopService(_bus);
            _context = new RedDotContext(_mail, _quests, _shop);

            _bridge = new RedDotBridge(new RedDotBridgeOptions
            {
                Bus = _bus,
                Context = _context,

                // Deliberately not PlayerPrefs: every run of the demo should start with
                // nothing seen, so the transient badges are actually visible.
                SeenPersistence = new InMemorySeenPersistence(),
                Log = message => Debug.LogWarning(message),
            });

            _binder = new RedDotBinder(_bridge);

            _driver = gameObject.GetComponent<RedDotDriver>();
            if (_driver == null)
            {
                _driver = gameObject.AddComponent<RedDotDriver>();
            }

            _driver.Attach(_bridge);

            UsingFallbackUI = !TryLoadPackage();
            if (UsingFallbackUI)
            {
                Debug.Log(
                    "[RedDotDemo] UI package '" + PackageName + "' not found -- using fallback UI. " +
                    "Author the package per docs/PACKAGE_SPEC.md and export it to " +
                    "Assets/FairyGUI-Packages/ to see the real thing.");
            }

            GRoot.inst.SetContentScaleFactor(DemoUIFactory.DesignWidth, DemoUIFactory.DesignHeight);

            BuildScreens();
            Show(MainScreen);

            Log(UsingFallbackUI ? "fallback UI (see docs/PACKAGE_SPEC.md)" : "UI package loaded");
        }

        private void OnDestroy()
        {
            Teardown();
        }

        public void Teardown()
        {
            _driver?.Detach();

            foreach (var screen in _screens.Values)
            {
                screen.RemoveFromParent();
                screen.Dispose();
            }

            _screens.Clear();
            _current = null;
            _output = null;

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
                var column = new DemoUIFactory.Column(screen, 140);
                column.AddRow(new List<GObject>
                {
                    DemoUIFactory.CreateTabButton("btnMail", "Mail"),
                    DemoUIFactory.CreateTabButton("btnQuests", "Quests"),
                    DemoUIFactory.CreateTabButton("btnShop", "Shop"),
                });
                column.Add(DemoUIFactory.CreateActionButton("btnApplyPatch", "Apply Lua patch"));
                column.Add(DemoUIFactory.CreateActionButton("btnStartOffer", "Start limited offer"));
                column.Add(DemoUIFactory.CreateActionButton("btnDumpTree", "Dump tree"));
                column.Add(DemoUIFactory.CreateOutputPanel("txtDebug", DemoUIFactory.ButtonWidth, 560));
            }

            _output = screen.GetChild("txtDebug") as GTextField;

            OnClick(screen, "btnMail", () => Show(MailScreen));
            OnClick(screen, "btnQuests", () => Show(QuestsScreen));
            OnClick(screen, "btnShop", () => Show(ShopScreen));

            OnClick(screen, "btnApplyPatch", ApplyPatch);
            OnClick(screen, "btnStartOffer", () =>
            {
                _bridge.RaiseEvent(LimitedOfferEvent);
                Log("raised " + LimitedOfferEvent);
            });
            OnClick(screen, "btnDumpTree", () => Debug.Log(_bridge.DebugDump()));

            Register(MainScreen, screen);
        }

        private void BuildMail()
        {
            var screen = CreateScreen(MailScreen, "Mail");

            if (UsingFallbackUI)
            {
                var column = new DemoUIFactory.Column(screen, 140);
                column.AddRow(new List<GObject>
                {
                    DemoUIFactory.CreateTabButton("btnInbox", "Inbox"),
                    DemoUIFactory.CreateTabButton("btnSystem", "System"),
                });
                column.Add(DemoUIFactory.CreateActionButton("btnAddMail", "Add mail"));
                column.Add(DemoUIFactory.CreateActionButton("btnReadOne", "Read one"));
                column.Add(DemoUIFactory.CreateActionButton("btnClaimAll", "Claim all"));
                column.Add(DemoUIFactory.CreateActionButton("btnBack", "Back"));
            }

            OnClick(screen, "btnAddMail", () => { _mail.Receive(); Log("mail received"); });
            OnClick(screen, "btnReadOne", () => { _mail.Read(); Log("mail read"); });
            OnClick(screen, "btnClaimAll", () => { _mail.ReadAll(); Log("inbox cleared"); });

            Register(MailScreen, screen);
        }

        private void BuildQuests()
        {
            var screen = CreateScreen(QuestsScreen, "Quests");

            if (UsingFallbackUI)
            {
                var column = new DemoUIFactory.Column(screen, 140);
                column.AddRow(new List<GObject>
                {
                    DemoUIFactory.CreateTabButton("btnDaily", "Daily"),
                    DemoUIFactory.CreateTabButton("btnAchievements", "Achievements"),
                });
                column.Add(DemoUIFactory.CreateActionButton("btnCompleteQuest", "Complete a quest"));
                column.Add(DemoUIFactory.CreateActionButton("btnClaimQuest", "Claim a quest"));
                column.Add(DemoUIFactory.CreateActionButton("btnUnlockAchievement", "Unlock an achievement"));
                column.Add(DemoUIFactory.CreateActionButton("btnBack", "Back"));
            }

            OnClick(screen, "btnCompleteQuest", () => { _quests.CompleteDaily(); Log("quest completed"); });
            OnClick(screen, "btnClaimQuest", () => { _quests.ClaimDaily(); Log("quest claimed"); });
            OnClick(screen, "btnUnlockAchievement", () => { _quests.UnlockAchievement(); Log("achievement unlocked"); });

            Register(QuestsScreen, screen);
        }

        private void BuildShop()
        {
            var screen = CreateScreen(ShopScreen, "Shop");

            if (UsingFallbackUI)
            {
                var column = new DemoUIFactory.Column(screen, 140);
                column.AddRow(new List<GObject>
                {
                    DemoUIFactory.CreateTabButton("btnDailyDeals", "Daily deals"),
                    DemoUIFactory.CreateTabButton("btnLimitedOffer", "Limited offer"),
                });
                column.Add(DemoUIFactory.CreateActionButton("btnNewDeal", "New deal arrives"));
                column.Add(DemoUIFactory.CreateActionButton("btnBack", "Back"));
            }

            OnClick(screen, "btnNewDeal", () =>
            {
                _shop.RefreshDailyDeals(_shop.NewDeals + 1);
                Log("deal added");
            });

            Register(ShopScreen, screen);
        }

        /// <summary>
        /// Binds a screen's badges and its back button, then files it away. Everything
        /// screen-specific has already happened by the time this runs.
        /// </summary>
        private void Register(string name, GComponent screen)
        {
            _screens[name] = screen;

            if (Badges.TryGetValue(name, out var badges))
            {
                foreach (var badge in badges)
                {
                    if (screen.GetChild(badge.Child) is GComponent host)
                    {
                        _binder.Bind(host, badge.Path, name);
                    }
                    else
                    {
                        Debug.LogWarning(
                            "[RedDotDemo] screen '" + name + "' has no child '" + badge.Child +
                            "'; that badge will not update. Check docs/PACKAGE_SPEC.md.");
                    }
                }
            }

            if (name != MainScreen)
            {
                OnClick(screen, "btnBack", () => Show(MainScreen));
            }
        }

        private void Show(string name)
        {
            if (!_screens.TryGetValue(name, out var screen))
            {
                return;
            }

            _current?.RemoveFromParent();

            GRoot.inst.AddChild(screen);
            _current = screen;
            CurrentScreen = name;

            // Opening a leaf section is what "the player has looked at this" means. The
            // main screen deliberately does not do it: reaching the Mail tab is not the
            // same as having read the system notice inside it.
            if (MarkSeenOnClick.Contains(name) && Badges.TryGetValue(name, out var badges))
            {
                foreach (var badge in badges)
                {
                    _bridge.MarkSeen(badge.Path);
                }
            }
        }

        #endregion

        #region Actions

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
                Log("patch applied, " + changed + " node(s) changed");
                Debug.Log("[RedDotDemo] after patch:\n" + _bridge.DebugDump());
            }
            catch (Exception exception)
            {
                Log("patch failed: " + exception.Message);
                Debug.LogException(exception);
            }
        }

        #endregion

        #region Helpers

        private static void OnClick(GComponent screen, string childName, Action handler)
        {
            var child = screen.GetChild(childName);
            if (child == null)
            {
                return;
            }

            child.onClick.Add(() => handler());
        }

        /// <summary>Appends a line to the on-screen debug panel, keeping the last few.</summary>
        private void Log(string message)
        {
            _log.Add(message);
            if (_log.Count > LogLines)
            {
                _log.RemoveRange(0, _log.Count - LogLines);
            }

            if (_output != null)
            {
                _output.text = string.Join("\n", _log);
            }
        }

        #endregion
    }
}
