using System.Collections.Generic;
using System.Linq;

namespace K2D2.Landing.Precision
{
    // One part of the vessel's tree, as CapsuleSide needs it.
    public struct CapsulePart
    {
        public string Id;
        public string ParentId;          // null for the root
        public bool IsDecoupler;
        public bool IsParachute;         // a usable chute (not cut)
        // Decouplers only: the neighbour (parent or child) the decoupler separates from, at its
        // explosive node; the decoupler stays with the other side. Null = unknown or an omni
        // decoupler: it separates from every neighbour and belongs to neither side. Empty = its
        // explosive node holds nothing: it separates nothing.
        public string DetachesFromId;
        public double MassT;
    }

    public struct CapsuleSideResult
    {
        public bool Ok;
        public string Reason;
        public HashSet<string> Ids;      // the capsule's parts
        // Capsule parts attached to a part that is not in the capsule: their faces toward the rest
        // of the vessel are occluded now and exposed after the separation.
        public HashSet<string> BoundaryIds;
        public bool WholeVessel;         // no decoupler separates the chutes from anything
        public int Parachutes;
        public double MassT;             // Σ MassT of the capsule's parts
    }

    // The capsule: the parts reached from the parachutes without crossing a decoupler's
    // separation. Every decoupler is cut at its explosive node, so the capsule is the smallest
    // piece holding the chutes (the decoupler nearest to them on every side). Refused when the
    // chutes sit on pieces that separate from each other. Pure, tested in EditMode.
    public static class CapsuleSide
    {
        public const string Prefix = "Cannot identify the capsule: ";

        public static CapsuleSideResult Find(IList<CapsulePart> parts)
        {
            if (parts == null || parts.Count == 0)
                return Refuse("the vessel has no parts.");
            var byId = new Dictionary<string, CapsulePart>();
            foreach (CapsulePart p in parts)
            {
                if (p.Id == null || byId.ContainsKey(p.Id))
                    return Refuse("two parts share the id " + (p.Id ?? "null") + ".");
                byId[p.Id] = p;
            }

            // Tree edges, without the ones a decoupler separates.
            var neighbours = byId.Keys.ToDictionary(id => id, id => new List<string>());
            foreach (CapsulePart p in parts)
            {
                if (p.ParentId == null || !byId.ContainsKey(p.ParentId))
                    continue;
                neighbours[p.Id].Add(p.ParentId);
                neighbours[p.ParentId].Add(p.Id);
            }
            // A decoupler whose explosive node holds a part that is not its neighbour in the tree:
            // treated as unknown.
            foreach (string id in byId.Keys.ToList())
            {
                CapsulePart p = byId[id];
                if (p.IsDecoupler && !string.IsNullOrEmpty(p.DetachesFromId) && !neighbours[id].Contains(p.DetachesFromId))
                {
                    p.DetachesFromId = null;
                    byId[id] = p;
                }
            }
            bool Cut(string a, string b) => Separates(byId[a], b) || Separates(byId[b], a);

            List<string> chutes = parts.Where(p => p.IsParachute).Select(p => p.Id).ToList();
            if (chutes.Count == 0)
                return Refuse("no parachute on this vessel.");

            var capsule = new HashSet<string> { chutes[0] };
            var queue = new Queue<string>(capsule);
            while (queue.Count > 0)
            {
                string id = queue.Dequeue();
                foreach (string n in neighbours[id])
                    if (!capsule.Contains(n) && !Cut(id, n))
                    {
                        capsule.Add(n);
                        queue.Enqueue(n);
                    }
            }

            int apart = chutes.Count(id => !capsule.Contains(id));
            if (apart > 0)
                return Refuse($"{apart} of the {chutes.Count} parachutes are on a part that a decoupler separates from the others.");

            var boundary = new HashSet<string>(capsule.Where(id => neighbours[id].Any(n => !capsule.Contains(n))));
            return new CapsuleSideResult
            {
                Ok = true,
                Ids = capsule,
                BoundaryIds = boundary,
                WholeVessel = capsule.Count == byId.Count,
                Parachutes = chutes.Count,
                MassT = capsule.Sum(id => byId[id].MassT),
            };
        }

        // Does this decoupler separate from that neighbour?
        static bool Separates(CapsulePart part, string neighbour) =>
            part.IsDecoupler && (part.DetachesFromId == null || part.DetachesFromId == neighbour);

        static CapsuleSideResult Refuse(string why) => new CapsuleSideResult { Ok = false, Reason = Prefix + why };
    }
}
