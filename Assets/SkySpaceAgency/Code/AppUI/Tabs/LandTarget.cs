using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.Game;
using KTools;
using SkySpaceAgency.Landing;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// One profile's landing target in the Land tab (the classic tab's target_settings_vac /
    /// target_settings_atmo): the target box (name and coordinates), Pick on map, Here, Coordinates
    /// (the latitude and longitude fields), the waypoint list and the pick status. Bound to its own
    /// LandingSettings, never rebound. The list and the pick follow LandingUI's rules (WaypointChoices,
    /// ReduxPlacement, TargetPoint, KscLocation): see LandTargets.
    /// </summary>
    public class LandTargetBlock : VisualElement
    {
        internal readonly LandingSettings Settings;
        internal readonly string Suffix;
        internal readonly Dictionary<string, ReduxWaypoint> Choices = new();
        internal string SelectedId;
        internal double SelectedLat, SelectedLon;
        internal bool? PickAvailable;

        public AppDropdown List { get; }
        public AppButton Pick { get; }
        public AppButton Here { get; }
        public AppButton Coordinates { get; }
        public AppNote Status { get; } = new AppNote();
        public VisualElement CoordinateFields { get; }
        readonly Label _name, _line, _arrow;

        public LandTargetBlock(LandingSettings settings, string suffix, Action<LandTargetBlock> onPick, Action<LandTargetBlock> onHere)
        {
            Settings = settings;
            Suffix = suffix;
            AddToClassList("ssa-land-target");
            name = "land-target" + suffix;

            Add(Ui.Label("Target", "ssa-field__label"));
            var box = Ui.Box("ssa-land-target__box");
            box.Add(new LandTargetIcon());
            var text = Ui.Box("ssa-land-target__text");
            _name = Ui.Label("", "ssa-land-target__name");
            _line = Ui.Label("", "ssa-land-target__line");
            text.Add(_name);
            text.Add(_line);
            box.Add(text);
            _arrow = Ui.Label("▾", "ssa-land-target__arrow");
            box.Add(_arrow);
            box.tooltip = "Choose the target among KSC, the point picked on the map and the waypoints of this body";
            // The box opens the waypoint list under it (the mockup has no separate list: this keeps the plan short).
            box.AddManipulator(new Clickable(() => ShowList(List.style.display == DisplayStyle.None)));
            Add(box);

            List = new AppDropdown(null, new List<string>(), -1);
            List.name = "waypoint-list" + suffix;
            List.Input.RegisterValueChangedCallback(e =>
            {
                if (e.newValue != null && Choices.TryGetValue(e.newValue, out var waypoint))
                {
                    SelectedId = string.IsNullOrEmpty(waypoint.id) ? null : waypoint.id;
                    SelectedLat = waypoint.latitude;
                    SelectedLon = waypoint.longitude;
                    Settings.target_latitude.V = (float)waypoint.latitude;
                    Settings.target_longitude.V = (float)waypoint.longitude;
                }
                Refresh();
            });
            List.AddToClassList("ssa-land-target__list");
            Add(List);
            ShowList(false);

            var row = new AppButtonRow();
            Pick = row.Add(LandTargets.PickText, () => onPick?.Invoke(this));
            Here = row.Add("Here", () => onHere?.Invoke(this));
            Coordinates = row.Add("Coordinates", () => ShowCoordinates(CoordinateFields.style.display == DisplayStyle.None));
            row.AddToClassList("ssa-land-target__buttons");
            Add(row);
            Add(Status);
            Status.Set("");


            CoordinateFields = Ui.Box("ssa-fields", "ssa-land-target__fields");
            CoordinateFields.Add(AppBind.Field("Latitude", settings.target_latitude, "°", "0.0000"));
            var lon = AppBind.Field("Longitude", settings.target_longitude, "°", "0.0000");
            lon.AddToClassList("ssa-field--last");
            CoordinateFields.Add(lon);
            Add(CoordinateFields);
            ShowCoordinates(false);

            settings.target_latitude.listeners += _ => Refresh();
            settings.target_longitude.listeners += _ => Refresh();
            Refresh();
        }

        public void ShowCoordinates(bool show)
        {
            CoordinateFields.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            Coordinates.EnableInClassList("ssa-land-button--on", show);
        }

        public void ShowList(bool show)
        {
            List.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            _arrow.text = show ? "▴" : "▾";
        }

        public void SetStatus(string text) => Status.Set(text);

        /// <summary>The box: the list's entry (without its coordinates), the saved coordinates and the entry's kind.</summary>
        public void Refresh()
        {
            string entry = List.Value;
            _name.text = DisplayName(entry);
            _line.text = CoordinatesText(Settings.target_latitude.V, Settings.target_longitude.V) + " · " + KindOf(entry);
        }

        public static string CoordinatesText(double lat, double lon) =>
            string.Format(CultureInfo.InvariantCulture, "{0:0.00}° {1} · {2:0.00}° {3}",
                Math.Abs(lat), lat >= 0 ? "N" : "S", Math.Abs(lon), lon >= 0 ? "E" : "W");

        public static string DisplayName(string entry)
        {
            if (string.IsNullOrEmpty(entry)) return "No target";
            if (entry.StartsWith(WaypointChoices.CustomName)) return "Custom coordinates";
            if (entry.StartsWith(WaypointChoices.MissionPrefix)) return entry.Substring(WaypointChoices.MissionPrefix.Length);
            return entry;
        }

        public static string KindOf(string entry)
        {
            if (string.IsNullOrEmpty(entry)) return "coordinates";
            if (entry.StartsWith(WaypointChoices.CustomName)) return "coordinates";
            if (entry.StartsWith(WaypointChoices.MissionPrefix)) return "mission waypoint";
            if (entry.StartsWith(WaypointChoices.KscName)) return "launch site";
            if (entry.StartsWith(WaypointChoices.PickedName)) return "picked on map";
            if (entry == "No body") return "no body";
            return "waypoint";
        }
    }

    /// <summary>
    /// The two target blocks and the map pick, as LandingUI's TargetBlock code does for the classic tab
    /// (same helpers, same log lines): the list is rebuilt for the shown profile (KSC, the picked point,
    /// the player's waypoints, the game's, else "Custom"), a rebuild never writes the target, a chosen
    /// player waypoint that moves takes the target with it, and a pick is cancelled when the tab or the
    /// window is left, the landing starts, the vessel, the view or the profile changes, or Escape.
    /// </summary>
    public class LandTargets
    {
        public const string PickText = "Pick on map";
        const string PickTooltip = "Click the body in the map view to set the landing target";
        const string WaitingText = "Click the body… (Esc)";
        const string UnavailableText = "Map picking unavailable in this game version";
        const string OpenMapText = "Open the map (M), then click the body.";
        const string ReduxAlsoSavedText = "Redux's Waypoints window also saved this click as a waypoint.";
        const float MapOpenTimeout = 3f;

        readonly Func<LandingPilot> _pilot;
        public LandTargetBlock Vac { get; }
        public LandTargetBlock Atmo { get; }

        ReduxWaypoint? _picked;
        LandTargetBlock _owner;
        bool _beginPending;
        float _beginDeadline;
        object _vessel;
        int _waypointsBefore = -1;
        LandTargetBlock _countCheck;
        float _kscRetry = -1;
        int _lastTickFrame = -1;

        public LandTargets(Func<LandingPilot> pilot, LandingSettings vac, LandingSettings atmo)
        {
            _pilot = pilot;
            Vac = new LandTargetBlock(vac, "_vac", OnPickClicked, OnHere);
            Atmo = new LandTargetBlock(atmo, "_atmo", OnPickClicked, OnHere);
        }

        LandingPilot P => _pilot();

        public bool Picking => _owner != null;

        void Info(string s) { var p = P; if (p != null) p.logger.LogInfo(s); }
        void Warn(string s) { var p = P; if (p != null) p.logger.LogWarning(s); }

        // ---- the list ----

        ReduxWaypoint? KscEntry(string bodyName)
        {
            string home = null;
            try { home = GameManager.Instance?.Game?.UniverseModel?.HomeWorld?.Name; }
            catch (Exception) { }
            if (home == null || home != bodyName) return null;
            float now = Time.realtimeSinceStartup;
            if (now < _kscRetry) return null;
            if (!KscLocation.TryGet(out string kscBody, out double rawLat, out double rawLon))
            {
                _kscRetry = now + 1f;
                return null;
            }
            _kscRetry = -1;
            if (kscBody != bodyName) return null;
            if (!TargetPoint.TryNormalize(rawLat, rawLon, out double lat, out double lon))
            {
                ReduxWaypoints.LogOnce(string.Format(CultureInfo.InvariantCulture,
                    "[Landing] target: KSC coordinates rejected ({0:R}, {1:R}), entry omitted.", rawLat, rawLon));
                return null;
            }
            return new ReduxWaypoint { name = WaypointChoices.KscName, body_name = kscBody, latitude = lat, longitude = lon };
        }

        void Follow(LandTargetBlock block, List<ReduxWaypoint> player, string bodyName)
        {
            var result = WaypointChoices.Follow(block.SelectedId, block.SelectedLat, block.SelectedLon,
                block.Settings.target_latitude.V, block.Settings.target_longitude.V, player);
            if (result.Action == WaypointChoices.FollowAction.Forget)
            {
                block.SelectedId = null;
                return;
            }
            if (result.Action != WaypointChoices.FollowAction.Follow) return;
            var w = result.Waypoint;
            Info(string.Format(CultureInfo.InvariantCulture,
                "[Landing] target: waypoint '{0}' moved from ({1:0.0000}, {2:0.0000}) to ({3:0.0000}, {4:0.0000}) on {5}, the target follows ({6} profile).",
                w.name, block.SelectedLat, block.SelectedLon, w.latitude, w.longitude, bodyName, block.Suffix.TrimStart('_')));
            block.SelectedLat = w.latitude;
            block.SelectedLon = w.longitude;
            block.Settings.target_latitude.V = (float)w.latitude;
            block.Settings.target_longitude.V = (float)w.longitude;
        }

        /// <summary>Rebuilds the shown block's list (LandingUI.buildWaypointList); never writes the target but to follow a moved waypoint.</summary>
        public void Rebuild(LandTargetBlock block)
        {
            var pilot = P;
            block.Choices.Clear();
            var body = pilot?.current_vessel?.currentBody();
            var labels = new List<string>();
            var built = new List<WaypointChoice>();
            if (body != null)
            {
                if (_picked != null && _picked.Value.body_name != body.Name) _picked = null;
                var player = ReduxWaypoints.available ? ReduxWaypoints.GetWaypointsForBody(body.Name) : new List<ReduxWaypoint>();
                Follow(block, player, body.Name);
                built = WaypointChoices.Build(player, ReduxWaypoints.GetGameWaypoints(), body.Name, KscEntry(body.Name), _picked);
                foreach (var c in built)
                {
                    block.Choices[c.Label] = c.Waypoint;
                    labels.Add(c.Label);
                }
            }

            string current = block.List.Value;
            if (!string.IsNullOrEmpty(current) && labels.Contains(current) && block.Choices.TryGetValue(current, out var shown)
                && WaypointChoices.IndexOfSaved(new List<WaypointChoice> { new WaypointChoice { Label = current, Waypoint = shown } },
                    block.Settings.target_latitude.V, block.Settings.target_longitude.V) == 0)
            {
                SetList(block, labels, current);
                return;
            }
            if (body == null)
            {
                SetList(block, new List<string> { "No body" }, "No body");
                return;
            }
            double lat = block.Settings.target_latitude.V, lon = block.Settings.target_longitude.V;
            int index = WaypointChoices.IndexOfSaved(built, lat, lon);
            string selected;
            if (index >= 0) selected = built[index].Label;
            else
            {
                selected = WaypointChoices.Custom(body.Name, lat, lon).Label;
                if (labels.Contains(selected)) selected += " #2";
                labels.Add(selected);
            }
            SetList(block, labels, selected);
        }

        static void SetList(LandTargetBlock block, List<string> labels, string selected)
        {
            var old = block.List.Input.choices;
            bool same = old != null && old.Count == labels.Count;
            if (same) for (int i = 0; i < labels.Count && same; i++) same = old[i] == labels[i];
            if (!same) block.List.Input.choices = labels;
            if (block.List.Value != selected) block.List.Input.SetValueWithoutNotify(selected);
            block.Refresh();
        }

        /// <summary>The editor and the sample: a list of fixed entries.</summary>
        public static void SetStaticList(LandTargetBlock block, List<string> labels, string selected) => SetList(block, labels, selected);

        // ---- the pick ----

        public void UpdatePickButton(LandTargetBlock block)
        {
            bool available = ReduxPlacement.Available;
            if (block.PickAvailable == available) return;
            block.PickAvailable = available;
            block.Pick.SetEnabled(available);
            block.Pick.tooltip = available ? PickTooltip : UnavailableText;
        }

        static GameState? CurrentState()
        {
            try { return GeneralTools.Game?.GlobalGameState?.GetGameState()?.GameState; }
            catch (Exception) { return null; }
        }

        static int CountPlayerWaypoints()
        {
            try { return ReduxWaypoints.GetWaypointsForBody(null).Count; }
            catch (Exception) { return -1; }
        }

        bool OpenMap()
        {
            try
            {
                var provider = GeneralTools.Game?.ViewController?.DataProvider;
                if (provider == null)
                {
                    ReduxWaypoints.LogOnce("[Landing] target: cannot open the map: ViewController.DataProvider is null.");
                    return false;
                }
                provider.SetMapEnabled(true);
                Info("[Landing] target: map view requested (ViewDataProvider.SetMapEnabled(true)).");
                return true;
            }
            catch (Exception e)
            {
                ReduxWaypoints.LogOnce($"[Landing] target: cannot open the map: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        void OnPickClicked(LandTargetBlock block)
        {
            if (P == null) return;
            if (_owner != null || ReduxPlacement.Waiting)
            {
                CancelPick("cancelled from the button");
                return;
            }
            if (!ReduxPlacement.Available)
            {
                block.SetStatus(UnavailableText);
                return;
            }
            block.SetStatus("");
            _owner = block;
            _vessel = P.current_vessel?.VesselComponent;
            _countCheck = null;
            _lastTickFrame = Time.frameCount;
            block.Pick.Text = WaitingText;
            if (CurrentState() == GameState.FlightView)
            {
                if (OpenMap())
                {
                    _beginPending = true;
                    _beginDeadline = Time.realtimeSinceStartup + MapOpenTimeout;
                    return;
                }
                block.SetStatus(OpenMapText);
            }
            StartPlacement();
        }

        void StartPlacement()
        {
            _beginPending = false;
            var block = _owner;
            if (block == null) return;
            _waypointsBefore = CountPlayerWaypoints();
            bool started = ReduxPlacement.Begin((body, lat, lon) => OnPicked(block, body, lat, lon), message => OnPickFailed(block, message));
            if (!started)
            {
                _owner = null;
                block.Pick.Text = PickText;
                block.SetStatus(UnavailableText);
            }
        }

        void OnPickFailed(LandTargetBlock block, string message)
        {
            if (_owner == block) _owner = null;
            _beginPending = false;
            block.Pick.Text = PickText;
            if (message != "cancelled") block.SetStatus(message);
        }

        void OnPicked(LandTargetBlock block, string body, double rawLat, double rawLon)
        {
            if (_owner == block) _owner = null;
            block.Pick.Text = PickText;
            _countCheck = block;
            if (!TargetPoint.TryNormalize(rawLat, rawLon, out double lat, out double lon))
            {
                Warn(string.Format(CultureInfo.InvariantCulture,
                    "[Landing] target: picked point rejected, not a valid position: raw ({0}, {1:R}, {2:R}).", body, rawLat, rawLon));
                block.SetStatus("Picked point is not a valid position: ignored.");
                return;
            }
            string current = P?.current_vessel?.currentBody()?.Name;
            string refusal = TargetPoint.Accept(body, current);
            if (refusal != null)
            {
                Info("[Landing] target: " + refusal);
                block.SetStatus(refusal);
                return;
            }
            _picked = new ReduxWaypoint { name = WaypointChoices.PickedName, body_name = body, latitude = lat, longitude = lon, from_mission = false };
            Rebuild(block);
            foreach (var entry in block.Choices)
                if (entry.Value.name == WaypointChoices.PickedName && !entry.Value.from_mission)
                {
                    block.List.Input.SetValueWithoutNotify(entry.Key);
                    break;
                }
            block.Settings.target_latitude.V = (float)lat;
            block.Settings.target_longitude.V = (float)lon;
            block.Refresh();
            Info(string.Format(CultureInfo.InvariantCulture,
                "[Landing] target: target set from the map to ({0:0.0000}, {1:0.0000}) on {2} ({3} profile).", lat, lon, body, block.Suffix.TrimStart('_')));
            block.SetStatus(string.Format(CultureInfo.InvariantCulture, "Target set to ({0:0.00}, {1:0.00}).", lat, lon));
        }

        /// <summary>Here: the target becomes the vessel's position (new in the new window: the mockup's button).</summary>
        void OnHere(LandTargetBlock block)
        {
            var vessel = P?.current_vessel?.VesselComponent;
            if (vessel == null) { block.SetStatus("No vessel."); return; }
            double rawLat, rawLon;
            try { rawLat = vessel.Latitude; rawLon = vessel.Longitude; }
            catch (Exception e) { block.SetStatus("Position not readable: " + e.Message); return; }
            if (!TargetPoint.TryNormalize(rawLat, rawLon, out double lat, out double lon))
            {
                block.SetStatus("The vessel's position is not valid: ignored.");
                return;
            }
            block.SelectedId = null;
            block.Settings.target_latitude.V = (float)lat;
            block.Settings.target_longitude.V = (float)lon;
            Info(string.Format(CultureInfo.InvariantCulture, "[Landing] target: target set to the vessel's position ({0:0.0000}, {1:0.0000}) ({2} profile).",
                lat, lon, block.Suffix.TrimStart('_')));
            block.SetStatus(string.Format(CultureInfo.InvariantCulture, "Target set under the vessel ({0:0.00}, {1:0.00}).", lat, lon));
            Rebuild(block);
        }

        public void CancelPick(string reason)
        {
            if (_owner == null && !ReduxPlacement.Waiting) return;
            var block = _owner;
            _owner = null;
            _beginPending = false;
            ReduxPlacement.Cancel();
            if (block != null) block.Pick.Text = PickText;
            Info($"[Landing] target: pick cancelled ({reason}).");
        }

        /// <summary>
        /// Every tick of the tab (LandingUI.UpdatePick). The tab only ticks while shown: a gap of more
        /// than a few frames since the last tick means it was hidden (or the window closed), which
        /// cancels a pick as the classic tab does.
        /// </summary>
        public void UpdatePick(LandTargetBlock visible)
        {
            int frame = Time.frameCount;
            bool gap = _lastTickFrame >= 0 && frame - _lastTickFrame > 3;
            _lastTickFrame = frame;
            ReduxPlacement.Tick();

            if (_countCheck != null)
            {
                var block = _countCheck;
                _countCheck = null;
                int after = CountPlayerWaypoints();
                if (_waypointsBefore >= 0 && after > _waypointsBefore)
                {
                    Warn($"[Landing] target: Redux's Waypoints window saved a waypoint from the pick (player waypoints {_waypointsBefore} -> {after}).");
                    block.SetStatus(string.IsNullOrEmpty(block.Status.text) ? ReduxAlsoSavedText : block.Status.text + "\n" + ReduxAlsoSavedText);
                }
            }

            if (_owner == null) return;
            var pilot = P;
            // Cancelled elsewhere (LandingPilot.onReset cancels Redux's placement through the classic page).
            if (!_beginPending && !ReduxPlacement.Waiting)
            {
                _owner.Pick.Text = PickText;
                _owner = null;
                return;
            }
            var state = CurrentState();
            string reason = null;
            if (gap) reason = "Land tab hidden";
            else if (pilot == null) reason = "no landing pilot";
            else if (pilot.isRunning) reason = "landing started";
            else if (!ReferenceEquals(pilot.current_vessel?.VesselComponent, _vessel)) reason = "vessel changed";
            else if (state != GameState.FlightView && state != GameState.Map3DView) reason = "left flight and map view";
            else if (_owner != visible) reason = "landing profile changed";
            else if (Input.GetKeyDown(KeyCode.Escape)) reason = "Escape";
            if (reason != null)
            {
                CancelPick(reason);
                return;
            }
            if (_beginPending)
            {
                if (state == GameState.Map3DView) StartPlacement();
                else if (Time.realtimeSinceStartup > _beginDeadline)
                {
                    Info("[Landing] target: the map view did not open, starting the pick anyway.");
                    _owner.SetStatus(OpenMapText);
                    StartPlacement();
                }
            }
        }
    }
}
