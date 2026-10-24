using System;
using System.Collections.Generic;
using System.Reflection;
using KSP.Api;
using KSP.Modules;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
using UnityEngine;

namespace K2D2.Rendezvous
{
    // One docking port of a vessel, read on the main thread.
    public class PortRead
    {
        public PartComponent Part;
        public Data_DockingNode Data;
        public PartComponentModule_DockingNode Module;
        public Module_DockingNode View;   // null when the vessel is not loaded
        public PortInfo Info;             // Position/Axis zero when the view cannot be read
        public Vector3d Up;               // the port's roll reference, celestial Zup; zero when unread
        public bool FrameRead;            // Position, Axis and Up come from the docking transform
        public PortCapture Capture;
    }

    // Game glue: docking ports, "control from here", and RCS, for the rendezvous pilot.
    // Main thread only: every call reads or writes the game's parts.
    //
    // From the IL of Ksp2Redux.dll (Redux 26w42c), see the Task 4 report:
    // - Module_DockingNode.GetDockingTransform() is private. It returns the private field
    //   _dockingTransform, or the part's own transform when that is null. The game docks along
    //   that transform's forward (ValidateDockingConfiguration: docker dot −dockee.forward), so
    //   forward is the port's axis and up its roll reference. It is a Unity transform: physics
    //   space, which IPhysicsSpaceProvider converts. The field is read here, so a port with no
    //   docking transform counts as unread instead of using the part's axes.
    // - Module_DockingNode.OnControlFromHere() is private: it sets the part's control transform
    //   to the port's (PartComponent.SetControlTransform(…, "dockingControlTransform")), then
    //   VesselComponent.SetControlOwner(part). It is invoked by reflection.
    public static class DockingParts
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        static readonly FieldInfo DockingTransformField = typeof(Module_DockingNode).GetField("_dockingTransform", Private);
        static readonly MethodInfo ControlFromHereMethod = typeof(Module_DockingNode).GetMethod("OnControlFromHere", Private, null, Type.EmptyTypes, null);

        static string Id(PartComponent part) => part?.GlobalId.Guid.ToString("D");

        // Every docking port of the vessel. Frames are read only when the vessel is loaded.
        public static List<PortRead> Ports(VesselComponent vessel)
        {
            var ports = new List<PortRead>();
            var owner = vessel?.SimulationObject?.PartOwner;
            if (owner?.Parts == null) return ports;

            var views = Views(vessel);
            ICoordinateSystem celestial = vessel.Orbit?.referenceBody?.SimulationObject?.transform?.celestialFrame;
            foreach (PartComponent part in owner.Parts)
            {
                if (part == null) continue;
                if (!part.TryGetModuleData<PartComponentModule_DockingNode, Data_DockingNode>(out Data_DockingNode data) || data == null)
                    continue;
                part.TryGetModule(out PartComponentModule_DockingNode module);
                views.TryGetValue(part, out Module_DockingNode view);

                var read = new PortRead { Part = part, Data = data, Module = module, View = view };
                Vector3d face = Vector3d.zero, axis = Vector3d.zero, up = Vector3d.zero;
                read.FrameRead = celestial != null && TryFrame(view, celestial, out face, out axis, out up);
                read.Up = up;
                bool ready = (module != null ? module.GetCurrentState() : data.CurrentState) == Data_DockingNode.DockingState.Ready;
                read.Info = DockingReads.Port(Id(part), data.NodeTypes, ready, data.IsGendered, data.IsGenderFemale,
                    face, axis, part.PartData == null ? 0 : DockingReads.SizeMetres(part.PartData.sizeCategory));
                read.Capture = new PortCapture
                {
                    AcquireMinFwdDot = data.AcquireMinFwdDot, CaptureMinFwdDot = data.CaptureMinFwdDot,
                    CaptureMaxRvel = data.CaptureMaxRvel, MinRollDot = data.MinRollDot, CaptureMinRollDot = data.CaptureMinRollDot,
                };
                ports.Add(read);
            }
            return ports;
        }

        public static PortRead Find(IEnumerable<PortRead> ports, string partId)
        {
            if (ports == null || string.IsNullOrEmpty(partId)) return null;
            foreach (var p in ports)
                if (p.Info.PartId == partId) return p;
            return null;
        }

        // Re-reads one port's frame (the target's port moves with it), celestial Zup.
        public static bool TryFrame(PortRead port, out Vector3d face, out Vector3d axis, out Vector3d up)
        {
            ICoordinateSystem celestial = port?.Part?.PartOwner?.SimulationObject?.Vessel?.Orbit?.referenceBody?.SimulationObject?.transform?.celestialFrame;
            face = axis = up = Vector3d.zero;
            return celestial != null && TryFrame(port.View, celestial, out face, out axis, out up);
        }

        static bool TryFrame(Module_DockingNode view, ICoordinateSystem celestial, out Vector3d face, out Vector3d axis, out Vector3d up)
        {
            face = axis = up = Vector3d.zero;
            try
            {
                if (view == null || DockingTransformField == null) return false;
                var t = DockingTransformField.GetValue(view) as Transform;
                IPhysicsSpaceProvider space = GeneralTools.Game?.UniverseView?.PhysicsSpace;
                if (t == null || space == null) return false;
                face = DockingReads.ZupFromYup(space.PhysicsToPosition(V(t.position), celestial));
                axis = DockingReads.ZupFromYup(space.PhysicsToVector(V(t.forward), celestial)).normalized;
                up = DockingReads.ZupFromYup(space.PhysicsToVector(V(t.up), celestial)).normalized;
                return true;
            }
            catch (Exception e)
            {
                K2D2_Plugin.logger.LogInfo($"[Docking] port frame unreadable: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        static Vector3d V(Vector3 v) => new Vector3d(v.x, v.y, v.z);

        // The view modules of a loaded vessel, by part.
        static Dictionary<PartComponent, Module_DockingNode> Views(VesselComponent vessel)
        {
            var views = new Dictionary<PartComponent, Module_DockingNode>();
            var behavior = vessel?.SimulationObject?.objVesselBehavior;
            if (behavior?.parts == null) return views;
            foreach (PartBehavior part in behavior.parts)
            {
                if (part == null || part.Model == null) continue;
                var module = part.GetModule<Module_DockingNode>();
                if (module != null) views[part.Model] = module;
            }
            return views;
        }

        public static bool IsLoaded(VesselComponent vessel) => vessel?.SimulationObject?.objVesselBehavior != null;

        public static bool IsDocked(PortRead port)
        {
            if (port?.Data == null) return false;
            var state = port.Module != null ? port.Module.GetCurrentState() : port.Data.CurrentState;
            return state == Data_DockingNode.DockingState.Docked;
        }

        // The game's own compatibility test, for the log next to PortChooser's.
        public static bool GameSaysCompatible(PortRead a, PortRead b) =>
            a?.Module != null && b?.Module != null && a.Module.IsCompatibleWith(b.Module);

        // The target's bounding radius about its centre of mass, from its parts' centres of mass.
        public static double TargetRadius(VesselComponent target)
        {
            ICoordinateSystem celestial = target?.Orbit?.referenceBody?.SimulationObject?.transform?.celestialFrame;
            var parts = target?.SimulationObject?.PartOwner?.Parts;
            if (celestial == null || parts == null) return DockingReads.PartHalfSize;
            var centres = new List<Vector3d>();
            foreach (PartComponent p in parts)
                if (p != null) centres.Add(Position.Reframed(p.CenterOfMass, celestial).localPosition);
            Vector3d centre = Position.Reframed(target.CenterOfMass, celestial).localPosition;
            return DockingReads.BoundingRadius(centres, centre);   // a distance: Yup or Zup alike
        }

        // ---- Control from here ----

        static string previousControlId;
        static VesselComponent controlVessel;

        // "Control from here" on our port. The control part before the first call is kept for
        // RestoreControl.
        public static bool ControlFromHere(VesselComponent ours, PortRead port)
        {
            if (ours == null || port?.Part == null) return false;
            if (controlVessel != ours)
            {
                previousControlId = Id(ours.GetControlOwner());
                controlVessel = ours;
            }
            string how = null;
            if (port.View != null && ControlFromHereMethod != null)
            {
                try
                {
                    ControlFromHereMethod.Invoke(port.View, null);
                    how = "the port's own action";
                }
                catch (Exception e)
                {
                    K2D2_Plugin.logger.LogInfo($"[Docking] the port's control action failed: {(e.InnerException ?? e).GetType().Name}: {(e.InnerException ?? e).Message}");
                }
            }
            if (how == null)
            {
                ours.SetControlOwner(port.Part);
                how = "SetControlOwner (without the port's control transform)";
            }
            bool ok = Id(ours.GetControlOwner()) == port.Info.PartId;
            K2D2_Plugin.logger.LogInfo($"[Docking] control from {port.Part.PartName} through {how}: " +
                (ok ? "done" : "the control part did not change") + $", previous control part {previousControlId ?? "none"}");
            return ok;
        }

        // Back to the control part before ControlFromHere, on the vessel that now holds it (after
        // a docking that is the merged vessel). Does nothing when ControlFromHere was not called.
        public static void RestoreControl(VesselComponent vessel)
        {
            string id = previousControlId;
            previousControlId = null;
            controlVessel = null;
            if (id == null || vessel?.SimulationObject?.PartOwner?.Parts == null) return;
            foreach (PartComponent p in vessel.SimulationObject.PartOwner.Parts)
                if (p != null && Id(p) == id)
                {
                    vessel.SetControlOwner(p);
                    K2D2_Plugin.logger.LogInfo($"[Docking] control restored to {p.PartName}");
                    return;
                }
            K2D2_Plugin.logger.LogInfo("[Docking] control not restored: the previous control part is not on this vessel");
        }

        // ---- RCS ----

        static KSPActionGroupState? rcsBefore;

        public static KSPActionGroupState RcsState(VesselComponent vessel) => vessel.GetActionGroupState(KSPActionGroup.RCS);

        // Turns the RCS action group on or off. The state before the first call is kept for RestoreRcs.
        public static void SetRcs(VesselComponent vessel, bool on)
        {
            if (vessel == null) return;
            if (rcsBefore == null) rcsBefore = RcsState(vessel);
            vessel.SetActionGroup(KSPActionGroup.RCS, on);
        }

        public static void RestoreRcs(VesselComponent vessel)
        {
            var before = rcsBefore;
            rcsBefore = null;
            if (vessel == null || before == null) return;
            vessel.SetActionGroup(KSPActionGroup.RCS, before == KSPActionGroupState.True);
            K2D2_Plugin.logger.LogInfo($"[Docking] RCS restored to {before}");
        }

        // The status of every RCS thruster of the vessel (Data_RCS.ThrustStatus).
        public static RcsStatus Thrusters(VesselComponent vessel)
        {
            var statuses = new List<Data_RCS.ThrusterStatusState>();
            var parts = vessel?.SimulationObject?.PartOwner?.Parts;
            if (parts != null)
                foreach (PartComponent p in parts)
                    if (p != null && p.TryGetModuleData<PartComponentModule_RCS, Data_RCS>(out Data_RCS rcs) && rcs?.ThrustStatus != null)
                        statuses.Add(rcs.ThrustStatus.GetValue());
            return RcsStatus.From(statuses);
        }
    }
}
