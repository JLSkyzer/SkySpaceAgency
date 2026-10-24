using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using K2D2.KSPService;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP.Sim.impl;
using KSP.Sim.Maneuver;
using UnityEngine;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace K2D2.Transfer
{
    // Game glue shared by the long pilots (TransferPilot, RendezvousPilot). Main thread only,
    // except the work a PilotJob runs.

    // One job on a worker thread at a time: pure code only (the game was read before). Polled
    // from Update, its completion runs on the main thread.
    internal sealed class PilotJob
    {
        Task task;
        CancellationTokenSource cts;
        Action<Task> done;
        Stopwatch watch;

        public bool Running => task != null;
        public object Tag { get; private set; }   // what the job is, for the caller
        public double LastMs { get; private set; } // the last finished job's duration

        public void Run<T>(Func<CancellationToken, T> work, Action<Task<T>> onDone, object tag = null)
        {
            Cancel();
            cts = new CancellationTokenSource();
            var token = cts.Token;
            Tag = tag;
            task = Task.Run(() => work(token), token);
            done = t => onDone((Task<T>)t);
            watch = Stopwatch.StartNew();
        }

        // Runs the completion of a finished job; an exception it throws goes to onError.
        public void Poll(Action<Exception> onError)
        {
            if (task == null || !task.IsCompleted)
                return;
            var d = done;
            var t = task;
            LastMs = watch.Elapsed.TotalMilliseconds;
            task = null;
            done = null;
            cts = null;
            Tag = null;
            try
            {
                d(t);
            }
            catch (Exception e)
            {
                onError(e);
            }
        }

        public void Cancel()
        {
            if (task == null)
                return;
            cts.Cancel();
            task = null;
            done = null;
            cts = null;
            Tag = null;
        }

        // Canceled and faulted told apart before reading Result.
        public static bool Result<T>(Task<T> t, out T result, out string error, out bool canceled)
        {
            result = default;
            error = null;
            canceled = false;
            if (t.IsCanceled)
            {
                canceled = true;
                return false;
            }
            if (t.IsFaulted)
            {
                Exception e = t.Exception?.GetBaseException();
                if (e is OperationCanceledException)
                {
                    canceled = true;
                    return false;
                }
                error = e == null ? "unknown error" : $"{e.GetType().Name}: {e.Message}";
                return false;
            }
            result = t.Result;
            return true;
        }
    }

    // The saved plan files in <mod>/plans/: written through a temporary file, never thrown from.
    internal static class PlanFiles
    {
        public static void Write(string path, string json, ILogger logger, string prefix)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(path))
                    File.Replace(tmp, path, null);
                else
                    File.Move(tmp, path);
            }
            catch (Exception e)
            {
                logger.LogWarning($"{prefix} could not save the plan: {e.GetType().Name}: {e.Message}");
            }
        }

        public static void Delete(string path, ILogger logger, string prefix)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    logger.LogInfo($"{prefix} saved plan deleted: {Path.GetFileName(path)}");
                }
            }
            catch (Exception e)
            {
                logger.LogWarning($"{prefix} could not delete the saved plan: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    // Reads of the active vessel that never throw.
    internal static class PilotVessel
    {
        // The game's active vessel now. KSPVessel.VesselComponent is refreshed only in
        // K2D2_Plugin.Update: inside a vessel-change message it may still be the previous vessel.
        public static VesselComponent Active()
        {
            try
            {
                return K2D2_Plugin.Instance?.current_vessel?.GetActiveSimVessel();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string IdOf(VesselComponent vessel)
        {
            try
            {
                var so = vessel?.SimulationObject;
                return so == null ? null : so.GlobalId.Guid.ToString("D");
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string ControlPartOf(VesselComponent vessel)
        {
            try
            {
                PartComponent part = vessel?.GetControlOwner();
                return part == null ? null : $"{part.Guid}";
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string ActiveId() => IdOf(Active());

        public static List<string> PartIds(VesselComponent vessel)
        {
            var ids = new List<string>();
            try
            {
                var parts = vessel?.SimulationObject?.PartOwner?.Parts;
                if (parts != null)
                    foreach (PartComponent p in parts)
                        if (p != null)
                            ids.Add(p.GlobalId.Guid.ToString("D"));
            }
            catch (Exception)
            {
                // Unreadable: no parts.
            }
            return ids;
        }

        // Seconds at full thrust for dv; NaN without thrust.
        public static double BurnSeconds(BurndV burnDv, double dv)
        {
            if (!(dv > 0))
                return 0;
            try
            {
                burnDv.Compute_Thrust();
                double thrust = burnDv.active_thrust;
                if (thrust <= 0)
                    thrust = burnDv.full_thrust.magnitude;
                return BurnTiming.Duration(dv, thrust, burnDv.mass, burnDv.active_isp);
            }
            catch (Exception)
            {
                return double.NaN;
            }
        }
    }

    // Prepare only: the one game node the pilot made for the player, from its creation (one
    // FixedUpdate after the removal of the previous one) to the verdict flown / removed unflown
    // (TransferFlight.NodeFlown).
    internal sealed class PrepareOnlyNode
    {
        readonly ManeuverCreator creator = new ManeuverCreator();
        readonly ILogger logger;
        readonly string prefix;
        double enginesOffSince = double.NaN;

        public bool Pending { get; private set; }   // being created
        public bool Waiting { get; private set; }   // on the plan, waiting for the player
        public Guid Id { get; private set; }
        public string Name { get; private set; }
        public double DeltaV { get; private set; }
        public double UT { get; private set; }
        // The vessel's state when the node was made: its coast tells whether the node was flown.
        public int Body { get; private set; }
        public Vector3d R { get; private set; }
        public Vector3d V { get; private set; }
        public double StateUT { get; private set; }

        public PrepareOnlyNode(ILogger logger, string prefix)
        {
            this.logger = logger;
            this.prefix = prefix;
        }

        // Forgets the node (it stays on the game's plan) and the engine timer.
        public void Clear()
        {
            Pending = Waiting = false;
            Id = Guid.Empty;
            enginesOffSince = double.NaN;
        }

        public void StopWaiting() => Waiting = Pending = false;

        public void ResetEngines() => enginesOffSince = double.NaN;

        // Removes our previous node (the player's own stay) and creates this one next FixedUpdate.
        // stillWanted: checked then; onCreated / onFailed: on the main thread.
        public void Make(string name, double nodeUT, double dv, int body, Vector3d r, Vector3d v, double stateUT,
            double prograde, double normal, double radial, Func<bool> stillWanted, Action onCreated, Action onFailed)
        {
            Pending = true;
            Name = name;
            DeltaV = dv;
            UT = nodeUT;
            Body = body;
            R = r;
            V = v;
            StateUT = stateUT;
            creator.Update();
            Remove();
            K2D2_Plugin.Instance.StartCoroutine(CreateNextFrame(nodeUT, prograde, normal, radial, stillWanted, onCreated, onFailed));
        }

        // A node made in the same frame as the removal is not registered (ManeuverCreator).
        IEnumerator CreateNextFrame(double ut, double prograde, double normal, double radial, Func<bool> stillWanted,
            Action onCreated, Action onFailed)
        {
            yield return new WaitForFixedUpdate();
            if (!stillWanted() || !Pending)
                yield break;
            ManeuverNodeData node = null;
            try
            {
                creator.Update();
                node = creator.CreateManeuverNodeAtUT(ut, prograde, normal * ManeuverCreator.GameNormalSign, radial);
            }
            catch (Exception e)
            {
                logger.LogWarning($"{prefix} prepare only: the node could not be made: {e.GetType().Name}: {e.Message}");
            }
            Pending = false;
            if (node == null)
            {
                onFailed();
                yield break;
            }
            Id = node.NodeID;
            Waiting = true;
            onCreated();
        }

        public ManeuverNodeData Find()
        {
            if (Id == Guid.Empty)
                return null;
            var nodes = K2D2_Plugin.Instance.current_vessel.GetManeuverNodes();
            if (nodes != null)
                foreach (var n in nodes)
                    if (n != null && n.NodeID.Equals(Id))
                        return n;
            return null;
        }

        public void Remove()
        {
            var node = Find();
            if (node != null)
            {
                creator.RemoveNode(node);
                logger.LogInfo($"{prefix} prepare only: our previous node {Id} removed");
            }
            Id = Guid.Empty;
        }

        // Flown, waiting or removed unflown. expected: our velocity on the coast the node was made
        // from, at now; bodyChanged: the vessel left that body.
        public NodeState Judge(bool bodyChanged, Vector3d expected, Vector3d velocity, double now)
        {
            bool onPlan = Find() != null;
            bool thrusting = false;
            try
            {
                thrusting = PilotVessel.Active()?.IsUnderEngineThrust() ?? false;
            }
            catch (Exception)
            {
                // Unknown: assume not thrusting.
            }
            double real = Time.realtimeSinceStartupAsDouble;
            if (thrusting)
                enginesOffSince = double.NaN;
            else if (double.IsNaN(enginesOffSince))
                enginesOffSince = real;
            double offSeconds = double.IsNaN(enginesOffSince) ? 0 : real - enginesOffSince;
            return TransferFlight.NodeFlown(onPlan, now > UT, offSeconds, DeltaV, bodyChanged, expected, velocity);
        }
    }
}
