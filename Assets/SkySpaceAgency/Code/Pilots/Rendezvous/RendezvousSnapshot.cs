using System;
using System.Collections.Generic;
using K2D2.Transfer;
using KSP.Api;
using KSP.Messages;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;

namespace K2D2.Rendezvous
{
    // Both vessels' states at one UT, around the same body, celestial Zup (as 4.1's VesselSnapshot).
    public struct RendezvousStates
    {
        public double UT;
        public int Body;                 // index in GameBodySystem.GameBodies()
        public Vector3d OurR, OurV;      // from the orbits: centre of mass relative to the body
        public Vector3d TargetR, TargetV;
        public Vector3d OurOmega, TargetOmega;   // rad/s, Zup (see DockingReads.ZupAngularFromYup)
    }

    // Game glue: the target, the vessel list, both vessels' states and the docking message.
    // Main thread only.
    //
    // From the IL of Ksp2Redux.dll (Redux 26w42c), see the Task 4 report:
    // - SetTargetByID(id) finds the sim object and sets TargetObject to it: after
    //   SetTargetByID(part) the target is the part (IsPart), not its vessel any more. The vessel
    //   is TargetVessel(): the part's PartOwner's vessel.
    // - A docking (Module_DockingNode.DockToModule → SpaceSimulation.CreateCombinedVesselSimObject)
    //   builds a new vessel with a new GUID (IGGuid.NewGuid), moves both vessels' parts to it
    //   (PartComponent.SetPartOwner: part ids are kept), makes it active (SetActiveVehicle, so a
    //   VesselChangedMessage), destroys both old vessels, and only then publishes
    //   VesselDockedMessage. Recognise the merged vessel by part ids, never by vessel id.
    public static class RendezvousSnapshot
    {
        public static VesselComponent Ours => K2D2_Plugin.Instance?.current_vessel?.VesselComponent;

        // The targeted vessel, whether the game's target is the vessel itself or one of its parts.
        public static VesselComponent TargetVessel(VesselComponent ours)
        {
            SimulationObjectModel target = ours?.TargetObject;
            if (target == null) return null;
            if (target.IsVessel) return target.Vessel;
            if (target.IsPart) return target.Part?.PartOwner?.SimulationObject?.Vessel;
            return null;
        }

        // The targeted part (a docking port, after SetTarget(ours, port)), or null.
        public static PartComponent TargetPart(VesselComponent ours)
        {
            SimulationObjectModel target = ours?.TargetObject;
            return target != null && target.IsPart ? target.Part : null;
        }

        // Targets a vessel or a part (a docking port of the target vessel, before the approach).
        public static bool SetTarget(VesselComponent ours, IGGuid id, string what)
        {
            if (ours == null) return false;
            bool ok = ours.SetTargetByID(id);
            K2D2_Plugin.logger.LogInfo($"[Rendezvous] target set to {what}: " + (ok ? "done" : "refused by the game") +
                $", target is now {(ours.TargetObject == null ? "none" : ours.TargetObject.IsPart ? "a part" : ours.TargetObject.IsVessel ? "a vessel" : "another object")}" +
                $" of {TargetVessel(ours)?.Name ?? "no vessel"}");
            return ok;
        }

        // Every other vessel, grouped by body, ours first (DockingReads.GroupByBody).
        public static List<(string body, List<VesselEntry> vessels)> VesselsByBody(VesselComponent ours)
        {
            var entries = new List<VesselEntry>();
            List<VesselComponent> all = GeneralTools.Game?.UniverseModel?.GetAllVessels();
            if (all != null)
                foreach (VesselComponent v in all)
                {
                    if (v?.SimulationObject == null) continue;
                    entries.Add(new VesselEntry
                    {
                        Id = v.GlobalId.Guid.ToString("D"),
                        Name = v.Name,
                        Body = v.Orbit?.referenceBody?.bodyName ?? v.mainBody?.bodyName,
                        IsDebris = v.IsDebris(),
                        IsOurs = ours != null && v.GlobalId.Equals(ours.GlobalId),
                    });
                }
            string ourBody = ours?.Orbit?.referenceBody?.bodyName;
            return DockingReads.GroupByBody(entries, ourBody);
        }

        public static VesselComponent FindVessel(string id)
        {
            List<VesselComponent> all = GeneralTools.Game?.UniverseModel?.GetAllVessels();
            if (all == null || string.IsNullOrEmpty(id)) return null;
            foreach (VesselComponent v in all)
                if (v?.SimulationObject != null && v.GlobalId.Guid.ToString("D") == id) return v;
            return null;
        }

        // Both states at the current UT, from the same calls as VesselSnapshot. Refused when the
        // target orbits another body (that is 4.5's job).
        public static bool TryRead(VesselComponent ours, VesselComponent target, out RendezvousStates s, out string error)
        {
            s = default;
            try
            {
                IKeplerPatch a = ours?.Orbit, b = target?.Orbit;
                if (a == null) { error = "No active vessel in flight."; return false; }
                if (b == null) { error = "No target vessel."; return false; }
                if (!ReferenceEquals(a.referenceBody, b.referenceBody))
                {
                    error = $"The target orbits {b.referenceBody?.bodyName}, not {a.referenceBody?.bodyName}.";
                    return false;
                }
                s.UT = GeneralTools.Current_UT;
                s.Body = GameBodySystem.IndexOf(GameBodySystem.GameBodies(), a.referenceBody);
                if (s.Body < 0) { error = "The reference body is not in the game's body list."; return false; }
                s.OurR = a.GetRelativePositionAtUTZup(s.UT);
                s.OurV = a.GetOrbitalVelocityAtUTZup(s.UT);
                s.TargetR = b.GetRelativePositionAtUTZup(s.UT);
                s.TargetV = b.GetOrbitalVelocityAtUTZup(s.UT);
                ICoordinateSystem celestial = a.referenceBody.SimulationObject.transform.celestialFrame;
                s.OurOmega = AngularVelocity(ours, celestial);
                s.TargetOmega = AngularVelocity(target, celestial);
                error = null;
                return true;
            }
            catch (Exception e)
            {
                error = $"Cannot read the vessels: {e.GetType().Name}: {e.Message}";
                K2D2_Plugin.logger.LogInfo("[Rendezvous] " + error);
                return false;
            }
        }

        // The vessel's angular velocity in the celestial frame, Zup; zero when unreadable.
        public static Vector3d AngularVelocity(VesselComponent vessel, ICoordinateSystem celestial)
        {
            ITransformFrame frame = celestial as ITransformFrame;
            if (vessel == null || frame?.motionFrame == null) return Vector3d.zero;
            KSP.Sim.AngularVelocity w = KSP.Sim.AngularVelocity.Reframed(vessel.AngularVelocity, frame.motionFrame);
            Vector3d yup = Vector.Reframed(w.relativeAngularVelocity, celestial).vector;
            return DockingReads.ZupAngularFromYup(yup);
        }

        // The vessel's centre of mass in the celestial frame, Zup.
        public static Vector3d CentreOfMass(VesselComponent vessel, ICoordinateSystem celestial) =>
            DockingReads.ZupFromYup(Position.Reframed(vessel.CenterOfMass, celestial).localPosition);

        // The guidance state for a pair of ports, both vessels loaded. error is the refusal
        // (DockingReads.LoadedRefusal), or why the read failed.
        public static bool TryDockState(VesselComponent ours, PortRead ourPort, VesselComponent target, PortRead targetPort,
            out DockState state, out string error)
        {
            state = default;
            error = DockingReads.LoadedRefusal(DockingParts.IsLoaded(ours), DockingParts.IsLoaded(target),
                DockingParts.TryFrame(ourPort, out Vector3d ourFace, out Vector3d ourAxis, out Vector3d ourUp),
                DockingParts.TryFrame(targetPort, out Vector3d theirFace, out Vector3d theirAxis, out Vector3d theirUp));
            if (error != null) return false;
            if (!TryRead(ours, target, out RendezvousStates s, out error)) return false;
            ICoordinateSystem celestial = ours.Orbit.referenceBody.SimulationObject.transform.celestialFrame;
            state = DockingReads.State(s.UT,
                CentreOfMass(ours, celestial), s.OurV, s.OurOmega, ourFace, ourAxis, ourUp,
                CentreOfMass(target, celestial), s.TargetV, s.TargetOmega, theirFace, theirAxis, theirUp);
            return true;
        }

        // ---- VesselDockedMessage ----

        static SubscriptionHandle dockedHandle;
        static bool subscribed;

        // True from a VesselDockedMessage until Unwatch or the next WatchDocking.
        public static bool DockedSeen { get; private set; }

        public static void WatchDocking()
        {
            DockedSeen = false;
            if (subscribed || GeneralTools.Game?.Messages == null) return;
            dockedHandle = GeneralTools.Game.Messages.Subscribe<VesselDockedMessage>(OnDocked);
            subscribed = true;
        }

        public static void UnwatchDocking()
        {
            DockedSeen = false;
            if (!subscribed) return;
            GeneralTools.Game?.Messages?.Unsubscribe(ref dockedHandle);
            subscribed = false;
        }

        static void OnDocked(MessageCenterMessage msg)
        {
            DockedSeen = true;
            var m = msg as VesselDockedMessage;
            K2D2_Plugin.logger.LogInfo($"[Docking] VesselDockedMessage: {m?.VesselOne?.Model?.Name ?? "?"} + {m?.VesselTwo?.Model?.Name ?? "?"}, " +
                $"active vessel {Ours?.Name ?? "none"} ({Ours?.SimulationObject?.PartOwner?.PartCount ?? 0} parts)");
        }
    }
}
