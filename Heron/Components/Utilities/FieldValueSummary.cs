using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace Heron
{
    /// <summary>
    /// Summarizes field usage across branches and value frequencies per field.
    /// Outputs:
    /// - Union of all field names and how many branches include each field.
    /// - For every branch/path: values for each union field (aligned) and the global count for that exact value.
    /// </summary>
    public class FieldValueSummary : HeronComponent
    {
        public FieldValueSummary()
            : base("Field/Value Summary", "FV Summary",
                  "Create a union set of all fields and report counts per field across branches. Also output per-branch values aligned to the union fields and the global frequency of those values.",
                  "Utilities")
        {
        }

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddTextParameter("Fields", "F", "Field names per branch.", GH_ParamAccess.tree);
            p.AddGenericParameter("Values", "V", "Values aligned with fields per branch.", GH_ParamAccess.tree);
            p.AddBooleanParameter("Case Sensitive", "CS", "Treat string field names and string values as case sensitive.", GH_ParamAccess.item, true);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddTextParameter("All Fields", "Fields", "Union of all field names across branches (sorted).", GH_ParamAccess.list);
            p.AddIntegerParameter("Field Branch Counts", "FCounts", "For each field in All Fields, number of branches that include that field.", GH_ParamAccess.list);
            p.AddTextParameter("Values On Path", "VPath", "For each path, the value for every field in All Fields order. Empty if field missing on that path.", GH_ParamAccess.tree);
            p.AddIntegerParameter("Counts For Path Values", "CPath", "For each path, the global frequency for the corresponding value of each field (0 if missing).", GH_ParamAccess.tree);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var fields = new GH_Structure<GH_String>();
            var values = new GH_Structure<IGH_Goo>();
            bool caseSensitive = true;

            // Use explicit generic overloads to reliably fetch trees
            if (!DA.GetDataTree<GH_String>(0, out fields)) return;
            if (!DA.GetDataTree<IGH_Goo>(1, out values)) return;
            DA.GetData(2, ref caseSensitive);

            var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

            // Union of paths (sorted by string for stability)
            var pathSet = new HashSet<GH_Path>(new GH_PathComparer());
            foreach (var pth in fields.Paths) pathSet.Add(pth);
            foreach (var pth in values.Paths) pathSet.Add(pth);
            var allPaths = new List<GH_Path>(pathSet);
            allPaths.Sort((a, b) => a.ToString().CompareTo(b.ToString()));

            // Aggregates
            var allFieldsSet = new HashSet<string>(comparer);
            var fieldBranchCounts = new Dictionary<string, int>(comparer);
            var fieldValueCounts = new Dictionary<string, Dictionary<string, int>>(comparer);
            var pathFieldValues = new Dictionary<GH_Path, Dictionary<string, string>>(new GH_PathComparer());

            // First pass: gather counts and store per-path field->value map
            foreach (var path in allPaths)
            {
                var fBranch = fields.PathExists(path) ? fields.get_Branch(path) : null;
                var vBranch = values.PathExists(path) ? values.get_Branch(path) : null;

                var map = new Dictionary<string, string>(comparer);
                var seenInBranch = new HashSet<string>(comparer);

                int fCount = fBranch?.Count ?? 0;
                int vCount = vBranch?.Count ?? 0;
                int count = Math.Min(fCount, vCount);

                for (int i = 0; i < count; i++)
                {
                    var fItem = fBranch[i] as GH_String;
                    if (fItem == null) continue;
                    string fieldName = fItem.Value ?? string.Empty;
                    if (string.IsNullOrEmpty(fieldName)) continue;

                    var vGoo = vBranch[i] as IGH_Goo;
                    string valueKey = ValueToKeyString(vGoo, caseSensitive);

                    // Per-path mapping (last occurrence wins if duplicates)
                    map[fieldName] = valueKey;

                    // Union fields and branch counts
                    allFieldsSet.Add(fieldName);
                    if (!seenInBranch.Contains(fieldName))
                    {
                        seenInBranch.Add(fieldName);
                        int cur;
                        fieldBranchCounts.TryGetValue(fieldName, out cur);
                        fieldBranchCounts[fieldName] = cur + 1;
                    }

                    // Value frequencies per field
                    Dictionary<string, int> valCounts;
                    if (!fieldValueCounts.TryGetValue(fieldName, out valCounts))
                    {
                        valCounts = new Dictionary<string, int>(comparer);
                        fieldValueCounts[fieldName] = valCounts;
                    }
                    int vcur;
                    valCounts.TryGetValue(valueKey, out vcur);
                    valCounts[valueKey] = vcur + 1;
                }

                pathFieldValues[path] = map;
            }

            // Create sorted All Fields list
            var allFields = new List<string>(allFieldsSet);
            allFields.Sort(StringComparer.Ordinal);

            // Output lists aligned with All Fields
            var fieldCountList = new List<int>(allFields.Count);
            for (int i = 0; i < allFields.Count; i++)
            {
                var f = allFields[i];
                int c;
                fieldBranchCounts.TryGetValue(f, out c);
                fieldCountList.Add(c);
            }

            // Build per-path trees aligned with All Fields
            var valuesOnPath = new GH_Structure<GH_String>();
            var countsOnPath = new GH_Structure<GH_Integer>();
            foreach (var path in allPaths)
            {
                Dictionary<string, string> map;
                pathFieldValues.TryGetValue(path, out map);
                if (map == null) map = new Dictionary<string, string>(comparer);

                var outVals = new List<GH_String>(allFields.Count);
                var outCnts = new List<GH_Integer>(allFields.Count);

                for (int i = 0; i < allFields.Count; i++)
                {
                    var fname = allFields[i];
                    string vstr;
                    map.TryGetValue(fname, out vstr);
                    if (vstr == null) vstr = string.Empty;
                    outVals.Add(new GH_String(vstr));

                    int cnt = 0;
                    Dictionary<string, int> valCounts;
                    if (fieldValueCounts.TryGetValue(fname, out valCounts))
                    {
                        int tmp;
                        if (valCounts.TryGetValue(vstr, out tmp)) cnt = tmp;
                    }
                    outCnts.Add(new GH_Integer(cnt));
                }

                valuesOnPath.AppendRange(outVals, path);
                countsOnPath.AppendRange(outCnts, path);
            }

            // Set outputs
            DA.SetDataList(0, allFields);
            DA.SetDataList(1, fieldCountList);
            DA.SetDataTree(2, valuesOnPath);
            DA.SetDataTree(3, countsOnPath);
        }

        private static string ValueToKeyString(object v, bool caseSensitive)
        {
            if (v == null) return string.Empty;

            // Unwrap GH_Goo
            var goo = v as IGH_Goo;
            if (goo != null)
            {
                var sv = goo.ScriptVariable();
                if (sv == null) return string.Empty;
                v = sv;
            }

            if (v is string s)
                return caseSensitive ? s : s.ToLowerInvariant();

            if (v is double d) return d.ToString("G17", CultureInfo.InvariantCulture);
            if (v is float f) return f.ToString("G9", CultureInfo.InvariantCulture);
            if (v is IFormattable fmt) return fmt.ToString(null, CultureInfo.InvariantCulture);

            return v.ToString() ?? string.Empty;
        }

        private class GH_PathComparer : IEqualityComparer<GH_Path>
        {
            public bool Equals(GH_Path a, GH_Path b)
            {
                if (ReferenceEquals(a, b)) return true;
                if (a == null || b == null) return false;
                return a.ToString() == b.ToString();
            }
            public int GetHashCode(GH_Path p) => p == null ? 0 : p.ToString().GetHashCode();
        }

        protected override System.Drawing.Bitmap Icon => Properties.Resources.shp;

        public override Guid ComponentGuid => new Guid("8F2F6A18-89F9-4C3E-9D5E-4E8C36EA6C6A");
    }
}
