using System.Collections.Generic;

namespace K2D2.Transfer
{
    // The entries of a body dropdown: every body, grouped by parent. Depth first from each root
    // (in index order); a body's children come right after it, nearest orbit first (orbital period,
    // then index), and each entry is indented by its depth. Pure: shared by the Transfer tab and
    // the Lift tab's transfer goal (4.2).
    public static class BodyChoices
    {
        public struct Entry
        {
            public int Index;    // in the BodySystem
            public int Depth;    // 0 for a root
            public string Name;
            public string Label; // the indented text shown in the dropdown
        }

        // No-break spaces: a text field keeps them where it might trim plain leading spaces.
        public const string Indent = "   ";

        public static List<Entry> Grouped(BodySystem s)
        {
            var result = new List<Entry>();
            if (s == null)
                return result;
            var children = new List<int>[s.Count];
            var roots = new List<int>();
            for (int i = 0; i < s.Count; i++)
            {
                int p = s[i].ParentIndex;
                if (p < 0)
                    roots.Add(i);
                else
                    (children[p] ?? (children[p] = new List<int>())).Add(i);
            }
            foreach (var list in children)
                list?.Sort((a, b) =>
                {
                    int c = s.OrbitalPeriod(a).CompareTo(s.OrbitalPeriod(b));
                    return c != 0 ? c : a.CompareTo(b);
                });

            var stack = new Stack<(int body, int depth)>();
            for (int r = roots.Count - 1; r >= 0; r--)
                stack.Push((roots[r], 0));
            while (stack.Count > 0)
            {
                var (body, depth) = stack.Pop();
                result.Add(new Entry
                {
                    Index = body,
                    Depth = depth,
                    Name = s[body].Name,
                    Label = Repeat(Indent, depth) + s[body].Name,
                });
                var kids = children[body];
                if (kids == null)
                    continue;
                for (int k = kids.Count - 1; k >= 0; k--)
                    stack.Push((kids[k], depth + 1));
            }
            return result;
        }

        public static List<string> Labels(IReadOnlyList<Entry> entries)
        {
            var labels = new List<string>(entries.Count);
            foreach (var e in entries)
                labels.Add(e.Label);
            return labels;
        }

        // The position in entries, or -1.
        public static int FindLabel(IReadOnlyList<Entry> entries, string label)
        {
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Label == label)
                    return i;
            return -1;
        }

        public static int FindName(IReadOnlyList<Entry> entries, string name)
        {
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Name == name)
                    return i;
            return -1;
        }

        static string Repeat(string text, int count)
        {
            if (count <= 0)
                return "";
            var sb = new System.Text.StringBuilder(text.Length * count);
            for (int i = 0; i < count; i++)
                sb.Append(text);
            return sb.ToString();
        }
    }
}
