using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using JetBrains.Annotations;
using SpaceWarp2.UI.API.Appbar;
using K2D2.UI;
using UitkForKsp2.API;
using UnityEngine;
using KTools;
using K2D2.KSPService;
using KSP.Game;
using KSP.Messages;
using K2D2.Controller;

using K2D2.Lift;
using K2D2.Landing;
using K2D2.Node;
using Redux.ExtraModTypes;
using UnityEngine.ResourceManagement.AsyncOperations;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace K2D2
{
    internal class L
    {
        public static void Log(string txt)
        {
            K2D2_Plugin.logger.LogInfo(txt);
        }

        public static void Vector3(string label, Vector3 value)
        {
            K2D2_Plugin.logger.LogInfo(label + " : " + StrTool.Vector3ToString(value));
        }
    }

    public class K2D2_Plugin : KerbalMod
    {


        private static string _assemblyFolder;
        private static string AssemblyFolder =>
            _assemblyFolder ??= Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        private static string _settingsPath;
        private static string SettingsPath =>
            _settingsPath ??= Path.Combine(AssemblyFolder, "skyspaceagency_settings.json");

        // Landing's Atmo/Vacuum profile split (see LandingSettings.cs/KTools.SettingsFile.cs) -
        // two genuinely separate physical files, so tuning one profile can never touch the other's
        // saved values. Same AssemblyFolder-relative pattern as SettingsPath above.
        private static string _atmoLandingSettingsPath;
        private static string AtmoLandingSettingsPath =>
            _atmoLandingSettingsPath ??= Path.Combine(AssemblyFolder, "skyspaceagency_landing_atmo.json");

        private static string _vacLandingSettingsPath;
        private static string VacLandingSettingsPath =>
            _vacLandingSettingsPath ??= Path.Combine(AssemblyFolder, "skyspaceagency_landing_vac.json");

        // VAB tools files (exports/, imports/, dumps/), next to the DLL like the settings files above.
        internal static string VabFolder => Path.Combine(AssemblyFolder, "vab");

        // Saved transfer plans, one file per vessel (plan_<vessel GUID>.json). An install must keep it.
        internal static string PlansFolder => Path.Combine(AssemblyFolder, "plans");


        /// Singleton instance of the plugin class
        [PublicAPI] public static K2D2_Plugin Instance { get; set; }

        // AppBar button IDs
        internal const string ToolbarFlightButtonID = "BTN-SkySpaceAgencyFlight";
        internal const string ToolbarOabButtonID = "BTN-SkySpaceAgencyOAB";
        internal const string ToolbarKscButtonID = "BTN-SkySpaceAgencyKSC";

        public static ILogger logger;

        public KSPVessel current_vessel = new KSPVessel();

        static bool loaded = false;

        K2D2Window main_window = null;

        // Read by the Land tab every UI tick: a waiting map pick is cancelled once the window closes.
        internal static bool IsMainWindowOpen => Instance?.main_window != null && Instance.main_window.IsWindowOpen;

        VabToolsWindow vab_window = null;

        public override void OnPreInitialized()
        {
            logger = SWLogger;
        }

        /// <summary>
        /// Runs when the mod is first initialized.
        /// </summary>
        public override void OnInitialized()
        {

            Instance = this;
            // SWMetadata.Folder is a System.IO.DirectoryInfo, not a string (confirmed via the current
            // SpaceWarpPluginDescriptor's real field type) - string-concatenating it works today via
            // DirectoryInfo's implicit ToString(), but .FullName is the correct, intended accessor.
            //
            // AssetsLoader.Bundle = AssetBundle.LoadFromFile(SWMetadata.Folder.FullName + "/assets/bundles/k2d2_ui.bundle");
            //
            // Switched UI loading from the old prebuilt AssetBundle above to Redux's Addressables system -
            // see AssetsLoader.LoadUxml() for the new loading call and the reasoning. The line above is
            // commented out (not deleted) so reverting is a one-line job if Addressables doesn't pan out
            // in testing; k2d2_ui.bundle itself is left untouched on disk either way.

            // K2UIFactoryRegistration.RegisterAll() used to go here - a reflection-based workaround
            // that manually registered every K2UI custom control's legacy UxmlFactory with Unity's
            // internal VisualElementFactoryRegistry, needed because Unity's automatic factory scan
            // never recognized a BepInEx-loaded mod DLL as a "user assembly". That whole class is
            // gone now: every K2UI control moved to [UxmlElement]/[UxmlAttribute] (Unity 6.6 removes
            // UxmlFactory entirely, so this was happening either way), and the newer attribute-based
            // registration is handled by Redux itself for mod assemblies - no manual step needed.

            var k2D2PilotsMgr = new K2D2PilotsMgr();
            SettingsFile.Init(this, SettingsPath);

            // Landing's Atmo/Vacuum profile split - two independent files, loaded up front here
            // (same as the main settings file above) so both are ready before LandingPilot's
            // constructor builds its two LandingSettings instances.
            SettingsFile.GetOrCreate("land_atmo", this, AtmoLandingSettingsPath);
            var landVacFile = SettingsFile.GetOrCreate("land_vac", this, VacLandingSettingsPath);

            // One-time migration: before this split, Landing's settings lived in the main file
            // (SettingsFile.Instance/k2d2_settings.json) under "land." keys - copy whatever's
            // already tuned there into the new VACUUM file specifically (not land_atmo), since
            // that's the profile that's actually been flown/tuned so far. CopyMissingKeys only
            // ever fills in keys land_vac doesn't already have, so this is a no-op on every launch
            // after the first (see its own comment in SettingsFile.cs) - never overwrites anything
            // tuned in land_vac since the split happened, and never touches land_atmo at all.
            int migrated = landVacFile.CopyMissingKeys(SettingsFile.Instance, "land.");
            if (migrated > 0)
                logger.LogInfo($"[K2D2_Plugin] Migrated {migrated} existing Landing setting(s) from the main settings file into skyspaceagency_landing_vac.json.");

            gameObject.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(gameObject);
            RegisterMessages();

            // create staging 
            new StagingPilot();

            pilots_manager.AddPilot(new NodeExPilot());
            pilots_manager.AddPilot(new LiftPilot());
            pilots_manager.AddPilot(new LandingPilot());
            pilots_manager.AddPilot(new AttitudePilot());
            // Transfer to a body: its tab comes with the Transfer page (TransferUI).
            pilots_manager.AddPilot(new K2D2.Transfer.TransferPilot());
            // Rendezvous and docking (4.3): its tab (RendezvousUI) replaces the old Dock tab.
            pilots_manager.AddPilot(new K2D2.Rendezvous.RendezvousPilot());

            // pilots_manager.AddPilot(new DronePilot());
            // pilots_manager.AddPilot(new AttitudePilot());
            // pilots_manager.AddPilot(new LiftController());
     
            // controllerManager.AddController(new WarpController());
            // pilots_manager.AddPilot(new DockingAssist());

            // The window is created lazily: since Redux 26w42 the mod is disabled in the main menu,
            // where OnInitialized runs, and the game then refuses to load our Addressables. A window
            // created from that null UXML stays empty forever. OnCampaignLoaded (enabled campaigns
            // only), the AppBar button and Alt-O all go through EnsureWindow.
            if (Enabled)
                EnsureWindow();

            // Register Flight AppBar button
            Appbar.RegisterAppButton(
                SWMetadata.Name,
                ToolbarFlightButtonID,
                AssetsLoader.LoadIcon("icon.png"),
                isOpen =>
                {
                    if (EnsureWindow())
                        main_window.IsWindowOpen = isOpen;
                }
            );

            // Register OAB AppBar Button: the VAB tools window (export, import, parts dump). A separate
            // window that only opens in the VAB (VabToolsWindow closes itself elsewhere); the flight
            // window above keeps its own scene gating (validScenes). It is created on first use and
            // borrows the main window's style sheets, so it needs no Addressables entry of its own.
            Appbar.RegisterOABAppButton(
                SWMetadata.Name + " VAB tools",
                ToolbarOabButtonID,
                AssetsLoader.LoadIcon("icon.png"),
                isOpen =>
                {
                    if (EnsureVabWindow())
                        vab_window.IsWindowOpen = isOpen;
                }
            );

            // Register KSC AppBar Button
            // Appbar.RegisterKSCAppButton(
            //     ModName,
            //     ToolbarKscButtonID,
            //     AssetManager.GetAsset<Texture2D>($"{ModGuid}/images/icon.png"),
            //     () => myFirstWindowController.IsWindowOpen = !myFirstWindowController.IsWindowOpen
            // );

            loaded = true;
        }

        public override void OnCampaignLoaded()
        {
            EnsureWindow();
        }

        // Creates the main window once its UXML can be loaded. Returns false, without creating an
        // empty window, while the game still refuses our Addressables (mod disabled).
        private bool EnsureWindow()
        {
            if (main_window != null) return true;

            var myFirstWindowUxml = AssetsLoader.LoadUxml("K2D2_Window.uxml");
            if (myFirstWindowUxml == null)
            {
                logger.LogWarning($"[K2D2_Plugin] Main window not created yet (mod enabled: {Enabled}).");
                return false;
            }

            // Create the window options object
            var windowOptions = new WindowOptions
            {
                // The ID of the window. It should be unique to your mod.
                WindowId = "SkySpaceAgency",
                // The transform of parent game object of the window.
                // If null, it will be created under the main canvas.
                Parent = null,
                // Whether or not the window can be hidden with F2.
                IsHidingEnabled = true,
                // Whether to disable game input when typing into text fields.
                DisableGameInputForTextFields = true,
                MoveOptions = new MoveOptions
                {
                    // Whether or not the window can be moved by dragging.
                    IsMovingEnabled = false,
                    // Whether or not the window can only be moved within the screen bounds.
                    CheckScreenBounds = true
                }
            };

            // Create the window
            var k2d2_window = Window.Create(windowOptions, myFirstWindowUxml);
            // Add a controller for the UI to the window's game object
            main_window = k2d2_window.gameObject.AddComponent<K2D2Window>();

            return true;
        }

        // Creates the VAB tools window on first use, like EnsureWindow (same Addressables caveat).
        private bool EnsureVabWindow()
        {
            if (vab_window != null) return true;

            var styleSource = AssetsLoader.LoadUxml("K2D2_Window.uxml");
            if (styleSource == null)
            {
                logger.LogWarning($"[K2D2_Plugin] VAB tools window not created yet (mod enabled: {Enabled}).");
                return false;
            }

            vab_window = VabToolsWindow.Create(styleSource);
            return true;
        }

        public override void OnPostInitialized()
        {
        }

        // AssetsLoader.LoadUxml() (a separate static class, not a KerbalMod subclass) needs to call
        // Assets.LoadAssetAsync<T>() to load UI Toolkit assets via Addressables, but the base
        // KerbalMonoBehaviour.Assets property is `protected` - confirmed via CS0122 build error - so it's
        // not reachable from outside a KerbalMod subclass's own code, even through an instance reference
        // like K2D2_Plugin.Instance.Assets. This thin public wrapper exposes just the one call
        // AssetsLoader needs without loosening protection on anything else.
        public AsyncOperationHandle<T> LoadAddressableAsset<T>(string address)
        {
            return Assets.LoadAssetAsync<T>(address);
        }


        private static GameState[] validScenes = { GameState.FlightView, GameState.Map3DView };

        //private static GameState last_game_state ;

        private static bool ValidScene()
        {
            // Update/FixedUpdate/LateUpdate run before OnInitialized, while the game instance exists
            // but its state machine is not set up yet: both used to throw NullReferenceException here.
            if (!loaded || GeneralTools.Game == null) return false;

            // Since Redux 26w42a a mod is installed but disabled in every campaign that does not
            // enable its default feature (and in the main menu). Redux hides our AppBar button
            // then, but Alt-O and the pilots run from Update, so they must check Enabled.
            if (Instance == null || !Instance.Enabled)
            {
                ResetControllers();
                return false;
            }

            // GameStateMachine has no GetState() method on Redux's current assemblies; the current
            // GameState comes from GetGameState().GameState instead.
            var gameState = GeneralTools.Game.GlobalGameState?.GetGameState();
            if (gameState == null) return false;
            var state = gameState.GameState;
            bool is_valid = validScenes.Contains(state);
            if (!is_valid)
            {
                ResetControllers();
            }
            return is_valid;
        }

        void Update()
        {
            // main_ui?.Update();

            Debug.developerConsoleVisible = false;
            // Update Models (even on non valid scenes)
            current_vessel.Update();

            /* TODO: Other mods interfacing
            if (K2D2OtherModsInterface.instance == null)
            {
                var other_mods = new K2D2OtherModsInterface();
                other_mods.CheckModsVersions();
            }
            */

            if (ValidScene())
            {
                // Debug.developerConsoleVisible = false;
                if (Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.O) && EnsureWindow())
                    main_window.IsWindowOpen = !main_window.IsWindowOpen;

                StagingPilot.Instance.Update();

                if (!StagingPilot.Instance.is_staging)
                {
                    // Update Controllers only if staging is not in progress
                    pilots_manager.UpdateControllers();
                }   
            }
            else
            {
                if (main_window != null && main_window.IsWindowOpen)
                    main_window.IsWindowOpen = false;
            }

            // A burn under physics time warp that was not updated this frame (pilot stopped, scene
            // change, staging pause) gives the warp back to 1x and the player's mode.
            KSPService.PhysicsWarp.ReleaseIfAbandoned();
        }

        // call on reset on controller, each on can reset it's status
        public static void ResetControllers()
        {
            if (!loaded) return;
            StagingPilot.Instance.onReset();
            Instance.pilots_manager.onReset(); 
        }

        public bool settings_visible = false;

        public PilotsManager pilots_manager = new PilotsManager();

        void FixedUpdate()
        {
            if (ValidScene())
            {
                pilots_manager.FixedUpdateControllers();
            }
        }

        private void LateUpdate()
        {
            if (ValidScene())
            {
                pilots_manager.LateUpdateControllers();
            }
        }

        private void RegisterMessages()
        {
            Game.Messages.Subscribe<GameStateChangedMessage>(msg =>
            {
                var message = (GameStateChangedMessage)msg;

                // if (message.CurrentState == GameState.FlightView)
                // {
                //     ShapeDrawer.Instance.can_draw = true;
                // }
                // else if (message.PreviousState == GameState.FlightView)
                // {
                //     ShapeDrawer.Instance.can_draw = false;
                // }
            });

            Game.Messages.Subscribe<VesselChangedMessage>(msg =>
            {
                var message = (VesselChangedMessage)msg;
                // The registry's nodes, and the Orbit plan in progress, belonged to the previous vessel.
                NodeTargets.Clear();
                OrbitPlanRun.Clear();
                // The landing pilot may survive this reset (the jettison split): it is told why.
                Landing.LandingPilot.reset_by_vessel_change = true;
                try
                {
                    ResetControllers();
                }
                finally
                {
                    Landing.LandingPilot.reset_by_vessel_change = false;
                }
            });
        }

        // Public API to enable or disable a Pilot / Page
        [PublicAPI] public bool isPilotEnabled(string pilotName)
        {
            return K2D2PilotsMgr.Instance.isPilotEnabled(pilotName);
        }

        [PublicAPI] public void EnableAllPilots(bool enabled)
        {
            K2D2PilotsMgr.Instance.EnableAllPilots(enabled);
        }

        [PublicAPI] public void EnablePilot(string pilotName, bool enabled)
        {
            K2D2PilotsMgr.Instance.EnablePilot(pilotName, enabled);
        }

        [PublicAPI] public List<string> GetPilotsNames()
        {
            return K2D2PilotsMgr.Instance.GetPilotsNames();
        }

        // Public API to perform a precision node execution using K2-D2
        [PublicAPI] public void FlyNode()
        {
            NodeExPilot.Instance.Start();
        }

        [PublicAPI] public void StopFlyNode()
        {
            NodeExPilot.Instance.Stop();
        }

        [PublicAPI] public bool IsFlyNodeRunning()
        {
            return NodeExPilot.Instance.isRunning;
        }

        // Public API to get the status of K2D2 (used by FlightPlan)
        [PublicAPI] public string GetStatus()
        {
            return NodeExPilot.Instance.ApiStatus();
        }
    }
}
