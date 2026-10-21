using System;
using System.Reflection;

namespace K2D2.Landing
{
    // Drives Redux's own click-to-place flow (PlayerWaypointManager.BeginPlacement(), reached by
    // reflection like ReduxWaypoints, the type being internal) so the Land tab can use it as a
    // target picker. Members confirmed by reflection on Ksp2Redux.dll 26w42c: Instance,
    // BeginPlacement(), CancelPlacement(), IsPlacementActive, and the events PlacementCompleted
    // (Action<string,double,double>), PlacementStateChanged (Action<bool>), PlacementFailed
    // (Action<string>). What the three completion arguments mean (body, latitude, longitude) is
    // inferred, not confirmed: TargetPoint validates them. Everything runs on the main thread
    // (Redux raises the events from its own Update).
    public static class ReduxPlacement
    {
        const BindingFlags instance_flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

        public static bool Waiting { get; private set; }

        public static bool Available => Resolve(out _);

        static Action<string, double, double> _on_picked;
        static Action<string> _on_failed;
        static string _body_at_begin;
        static int _count_before;

        // Manager we are subscribed on (null when not subscribed), and the exact delegates added.
        static object _subscribed_on;
        static Action<string, double, double> _completed_handler;
        static Action<bool> _state_handler;
        static Action<string> _failed_handler;

        static bool Resolve(out object manager)
        {
            manager = null;
            try
            {
                var type = ReduxWaypoints.manager_type;
                var instance = ReduxWaypoints.GetManagerInstance();
                if (type == null || instance == null)
                {
                    ReduxWaypoints.LogOnce("[Landing] target: Redux placement unavailable: PlayerWaypointManager or its Instance not found.");
                    return false;
                }

                string missing = null;
                if (type.GetMethod("BeginPlacement", instance_flags, null, Type.EmptyTypes, null) == null) missing = "BeginPlacement()";
                else if (type.GetMethod("CancelPlacement", instance_flags, null, Type.EmptyTypes, null) == null) missing = "CancelPlacement()";
                else if (type.GetProperty("IsPlacementActive", instance_flags) == null) missing = "IsPlacementActive";
                else if (type.GetEvent("PlacementCompleted", instance_flags)?.EventHandlerType != typeof(Action<string, double, double>)) missing = "PlacementCompleted";
                else if (type.GetEvent("PlacementFailed", instance_flags)?.EventHandlerType != typeof(Action<string>)) missing = "PlacementFailed";
                else if (type.GetEvent("PlacementStateChanged", instance_flags)?.EventHandlerType != typeof(Action<bool>)) missing = "PlacementStateChanged";

                if (missing != null)
                {
                    ReduxWaypoints.LogOnce("[Landing] target: Redux placement unavailable: member " + missing + " missing or changed.");
                    return false;
                }
                manager = instance;
                return true;
            }
            catch (Exception e)
            {
                ReduxWaypoints.LogOnce("[Landing] target: Redux placement unavailable: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
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
                var type = manager.GetType();
                AddHandler(type, manager, "PlacementCompleted", _completed_handler);
                AddHandler(type, manager, "PlacementStateChanged", _state_handler);
                AddHandler(type, manager, "PlacementFailed", _failed_handler);
                _subscribed_on = manager;
                Waiting = true;

                type.GetMethod("BeginPlacement", instance_flags, null, Type.EmptyTypes, null).Invoke(manager, null);
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
            try
            {
                var type = manager.GetType();
                bool active = (bool)type.GetProperty("IsPlacementActive", instance_flags).GetValue(manager);
                if (active)
                    type.GetMethod("CancelPlacement", instance_flags, null, Type.EmptyTypes, null).Invoke(manager, null);
            }
            catch (Exception e)
            {
                ReduxWaypoints.LogOnce($"[Landing] target: CancelPlacement failed: {e.GetType().Name}: {e.Message}");
            }
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
            Cleanup();
            K2D2_Plugin.logger.LogWarning($"[Landing] target: placement failed: {message}");
            SafeCall(() => failed?.Invoke(message));
        }

        static void OnStateChanged(bool active)
        {
            if (active || !Waiting) return;
            // Placement ended without a completion or failure of ours: the player cancelled in Redux.
            var failed = _on_failed;
            Cleanup();
            K2D2_Plugin.logger.LogInfo("[Landing] target: placement ended without a point (cancelled).");
            SafeCall(() => failed?.Invoke("cancelled"));
        }

        // Same body filter before and after, so the two counts compare like with like.
        static int CountPlayerWaypoints() => ReduxWaypoints.GetWaypointsForBody(_body_at_begin).Count;

        static void Cleanup()
        {
            object manager = _subscribed_on;
            Waiting = false;
            _subscribed_on = null;
            if (manager != null)
            {
                var type = manager.GetType();
                RemoveHandler(type, manager, "PlacementCompleted", _completed_handler);
                RemoveHandler(type, manager, "PlacementStateChanged", _state_handler);
                RemoveHandler(type, manager, "PlacementFailed", _failed_handler);
            }
            _completed_handler = null;
            _state_handler = null;
            _failed_handler = null;
            _on_picked = null;
            _on_failed = null;
        }

        static void AddHandler(Type type, object manager, string event_name, Delegate handler)
            => type.GetEvent(event_name, instance_flags).GetAddMethod(true).Invoke(manager, new object[] { handler });

        static void RemoveHandler(Type type, object manager, string event_name, Delegate handler)
        {
            if (handler == null) return;
            try { type.GetEvent(event_name, instance_flags).GetRemoveMethod(true).Invoke(manager, new object[] { handler }); }
            catch (Exception e) { ReduxWaypoints.LogOnce($"[Landing] target: could not unsubscribe {event_name}: {e.Message}"); }
        }

        // A callback raised inside Redux's own event must not throw back into the game.
        static void SafeCall(Action action)
        {
            try { action(); }
            catch (Exception e) { K2D2_Plugin.logger.LogWarning($"[Landing] target: pick callback threw: {e.GetType().Name}: {e.Message}"); }
        }
    }
}
