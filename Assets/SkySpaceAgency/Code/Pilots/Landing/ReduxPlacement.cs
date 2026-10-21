using System;
using System.Reflection;

namespace K2D2.Landing
{
    // Drives Redux's own click-to-place flow (PlayerWaypointManager.BeginPlacement(), reached by
    // reflection like ReduxWaypoints, the type being internal) so the Land tab can use it as a
    // target picker. Members confirmed by reflection on Ksp2Redux.dll 26w42c: Instance,
    // BeginPlacement(), CancelPlacement(), IsPlacementActive, and the events PlacementCompleted
    // (Action<string,double,double>), PlacementStateChanged (Action<bool>), PlacementFailed
    // (Action<string>). The first completion argument is the body name (IL of
    // PlayerWaypointManager.Update); latitude and longitude follow. Everything runs on the main
    // thread (Redux raises the events from its own Update).
    public static class ReduxPlacement
    {
        const BindingFlags instance_flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

        public static bool Waiting { get; private set; }

        public static bool Available => Resolve(out _);

        static Action<string, double, double> _on_picked;
        static Action<string> _on_failed;
        static string _body_at_begin;
        static int _count_before;
        // Frame in which Redux reported the end of placement (PlacementStateChanged(false)), or -1.
        static int _ended_frame = -1;
        // Frame of the last Begin: Redux may only activate placement later in that frame, so a Tick in
        // the same frame must not read IsPlacementActive == false as a cancellation.
        static int _begin_frame = -1;

        // Manager we are subscribed on (null when not subscribed), and the exact delegates added.
        static object _subscribed_on;
        static Action<string, double, double> _completed_handler;
        static Action<bool> _state_handler;
        static Action<string> _failed_handler;

        // Member lookups on the (fixed) manager type are done once; only Instance is read per call.
        static bool _members_checked;
        static bool _members_ok;
        static MethodInfo _begin_method;
        static MethodInfo _cancel_method;
        static PropertyInfo _active_property;
        static EventInfo _completed_event;
        static EventInfo _state_event;
        static EventInfo _failed_event;

        static bool ResolveMembers()
        {
            if (_members_checked)
                return _members_ok;
            var type = ReduxWaypoints.manager_type;
            if (type == null)
            {
                ReduxWaypoints.LogOnce("[Landing] target: Redux placement unavailable: PlayerWaypointManager not found.");
                return false;
            }
            _members_checked = true;
            try
            {
                _begin_method = type.GetMethod("BeginPlacement", instance_flags, null, Type.EmptyTypes, null);
                _cancel_method = type.GetMethod("CancelPlacement", instance_flags, null, Type.EmptyTypes, null);
                _active_property = type.GetProperty("IsPlacementActive", instance_flags);
                _completed_event = type.GetEvent("PlacementCompleted", instance_flags);
                _state_event = type.GetEvent("PlacementStateChanged", instance_flags);
                _failed_event = type.GetEvent("PlacementFailed", instance_flags);

                string missing = null;
                if (_begin_method == null) missing = "BeginPlacement()";
                else if (_cancel_method == null) missing = "CancelPlacement()";
                else if (_active_property == null) missing = "IsPlacementActive";
                else if (_completed_event?.EventHandlerType != typeof(Action<string, double, double>)) missing = "PlacementCompleted";
                else if (_failed_event?.EventHandlerType != typeof(Action<string>)) missing = "PlacementFailed";
                else if (_state_event?.EventHandlerType != typeof(Action<bool>)) missing = "PlacementStateChanged";

                if (missing != null)
                    ReduxWaypoints.LogOnce("[Landing] target: Redux placement unavailable: member " + missing + " missing or changed.");
                _members_ok = missing == null;
            }
            catch (Exception e)
            {
                ReduxWaypoints.LogOnce("[Landing] target: Redux placement unavailable: " + e.GetType().Name + ": " + e.Message);
                _members_ok = false;
            }
            return _members_ok;
        }

        static bool Resolve(out object manager)
        {
            manager = null;
            if (!ResolveMembers())
                return false;
            try
            {
                manager = ReduxWaypoints.GetManagerInstance();
            }
            catch (Exception e)
            {
                ReduxWaypoints.LogOnce("[Landing] target: Redux placement unavailable: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
            if (manager == null)
            {
                ReduxWaypoints.LogOnce("[Landing] target: Redux placement unavailable: PlayerWaypointManager.Instance is null.");
                return false;
            }
            return true;
        }

        public static bool Begin(Action<string, double, double> onPicked, Action<string> onFailed)
        {
            if (!Resolve(out object manager))
                return false;

            if (Waiting)
                Cancel();

            try
            {
                _on_picked = onPicked;
                _on_failed = onFailed;
                _body_at_begin = K2D2_Plugin.Instance?.current_vessel?.currentBody()?.Name;
                _count_before = CountPlayerWaypoints();

                _completed_handler = OnCompleted;
                _state_handler = OnStateChanged;
                _failed_handler = OnFailed;
                _ended_frame = -1;
                _begin_frame = UnityEngine.Time.frameCount;
                // Set before the first AddHandler: if a later one throws, Cleanup still removes the
                // earlier ones (removing a delegate that was never added is a no-op).
                _subscribed_on = manager;
                Waiting = true;
                AddHandler(_completed_event, manager, _completed_handler);
                AddHandler(_state_event, manager, _state_handler);
                AddHandler(_failed_event, manager, _failed_handler);

                _begin_method.Invoke(manager, null);
                K2D2_Plugin.logger.LogInfo($"[Landing] target: placement started (player waypoints on {_body_at_begin ?? "all bodies"}: {_count_before}).");
                return true;
            }
            catch (Exception e)
            {
                var failed = _on_failed;
                string message = (e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e).Message;
                K2D2_Plugin.logger.LogWarning($"[Landing] target: BeginPlacement failed: {e.GetType().Name}: {message}");
                Cleanup();
                SafeCall(() => failed?.Invoke("Map picking failed: " + message));
                return true;
            }
        }

        public static void Cancel()
        {
            if (!Waiting && _subscribed_on == null)
                return;

            object manager = _subscribed_on;
            bool was_waiting = Waiting;
            // Unsubscribe first: CancelPlacement raises PlacementStateChanged(false), which must not
            // be reported as a cancellation of the pick we are abandoning ourselves.
            Cleanup();
            if (!was_waiting || manager == null)
                return;
            CancelIfActive(manager);
        }

        static void CancelIfActive(object manager)
        {
            if (manager == null)
                return;
            try
            {
                if ((bool)_active_property.GetValue(manager))
                    _cancel_method.Invoke(manager, null);
            }
            catch (Exception e)
            {
                ReduxWaypoints.LogOnce($"[Landing] target: CancelPlacement failed: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// To be called every UI tick (by the Land tab, Task 4). Redux raises PlacementStateChanged(false)
        /// BEFORE PlacementCompleted on a successful click (IL of PlayerWaypointManager.Update), so a
        /// state change alone is not a cancellation: it is only recorded. A pick that is still waiting
        /// once the frame has moved on (or once IsPlacementActive reads false) was cancelled in Redux
        /// (right-click, Escape, scene change): clean up and report onFailed("cancelled"). A Completed
        /// raised in the same call stack as the state change always wins, as it runs before any Tick.
        /// IsPlacementActive is not read in the frame of Begin (Redux may activate placement later in
        /// that frame); if reading it throws, the pick is abandoned and reported through onFailed.
        /// </summary>
        public static void Tick()
        {
            if (!Waiting)
                return;
            bool ended = _ended_frame >= 0 && UnityEngine.Time.frameCount > _ended_frame;
            if (!ended && UnityEngine.Time.frameCount != _begin_frame)
            {
                try
                {
                    ended = !(bool)_active_property.GetValue(_subscribed_on);
                }
                catch (Exception e)
                {
                    // Cannot tell whether Redux is still waiting: give the pick up instead of waiting forever.
                    var on_failed = _on_failed;
                    object manager = _subscribed_on;
                    Cleanup();
                    CancelIfActive(manager);
                    string message = (e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e).Message;
                    K2D2_Plugin.logger.LogWarning($"[Landing] target: could not read IsPlacementActive, pick abandoned: {e.GetType().Name}: {message}");
                    SafeCall(() => on_failed?.Invoke("Map picking failed: could not read the placement state (" + message + ")."));
                    return;
                }
            }
            if (!ended)
                return;

            var failed = _on_failed;
            Cleanup();
            K2D2_Plugin.logger.LogInfo("[Landing] target: placement ended without a point (cancelled).");
            SafeCall(() => failed?.Invoke("cancelled"));
        }

        static void OnCompleted(string a, double b, double c)
        {
            if (!Waiting) return;
            var picked = _on_picked;
            int before = _count_before;
            Cleanup();
            try
            {
                int after = CountPlayerWaypoints();
                K2D2_Plugin.logger.LogInfo($"[Landing] target: placement completed raw ({a}, {b:R}, {c:R}), player waypoints {before} -> {after}");
            }
            catch (Exception e)
            {
                ReduxWaypoints.LogOnce($"[Landing] target: could not count player waypoints: {e.Message}");
            }
            SafeCall(() => picked?.Invoke(a, b, c));
        }

        static void OnFailed(string message)
        {
            if (!Waiting) return;
            var failed = _on_failed;
            object manager = _subscribed_on;
            Cleanup();
            // Redux may leave placement active after a failure.
            CancelIfActive(manager);
            K2D2_Plugin.logger.LogWarning($"[Landing] target: placement failed: {message}");
            SafeCall(() => failed?.Invoke(message));
        }

        static void OnStateChanged(bool active)
        {
            // Only recorded: on a successful click this fires before PlacementCompleted. Tick() decides.
            if (!active && Waiting)
                _ended_frame = UnityEngine.Time.frameCount;
        }

        // Same body filter before and after, so the two counts compare like with like.
        static int CountPlayerWaypoints() => ReduxWaypoints.GetWaypointsForBody(_body_at_begin).Count;

        static void Cleanup()
        {
            object manager = _subscribed_on;
            Waiting = false;
            _subscribed_on = null;
            _ended_frame = -1;
            _begin_frame = -1;
            if (manager != null)
            {
                RemoveHandler(_completed_event, manager, _completed_handler);
                RemoveHandler(_state_event, manager, _state_handler);
                RemoveHandler(_failed_event, manager, _failed_handler);
            }
            _completed_handler = null;
            _state_handler = null;
            _failed_handler = null;
            _on_picked = null;
            _on_failed = null;
        }

        static void AddHandler(EventInfo ev, object manager, Delegate handler)
            => ev.GetAddMethod(true).Invoke(manager, new object[] { handler });

        static void RemoveHandler(EventInfo ev, object manager, Delegate handler)
        {
            if (handler == null) return;
            try { ev.GetRemoveMethod(true).Invoke(manager, new object[] { handler }); }
            catch (Exception e) { ReduxWaypoints.LogOnce($"[Landing] target: could not unsubscribe {ev.Name}: {e.Message}"); }
        }

        // A callback raised inside Redux's own event must not throw back into the game.
        static void SafeCall(Action action)
        {
            try { action(); }
            catch (Exception e) { K2D2_Plugin.logger.LogWarning($"[Landing] target: pick callback threw: {e.GetType().Name}: {e.Message}"); }
        }
    }
}
