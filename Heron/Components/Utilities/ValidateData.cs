using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace Heron
{
    /// <summary>
    /// Validates per-branch alignment and content of fields, values, and geometry trees.
    /// - Drops branches with hard errors (missing/empty branches, mismatched counts, unrecoverable invalid values, or no valid geometry).
    /// - Optionally replaces invalid values with NaN and keeps the branch.
    /// - Filters invalid geometry items but keeps the branch if at least one valid geometry remains.
    /// </summary>
    public class ValidateData : HeronComponent
    {
        public ValidateData()
            : base("Validate Data", "Validate",
                  "Validate fields, values, and geometry trees. Optionally replace invalid values with NaN and clean invalid geometry while keeping valid branches.",
                  "Utilities")
        {
        }

        public override GH_Exposure Exposure => GH_Exposure.tertiary;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddTextParameter("Fields", "F", "Field names per branch.", GH_ParamAccess.tree);
            p.AddGenericParameter("Values", "V", "Values aligned with fields per branch.", GH_ParamAccess.tree);
            p.AddGenericParameter("Geometry", "G", "Geometry per branch. Invalid items will be removed.", GH_ParamAccess.tree);
            p.AddBooleanParameter("Replace Invalid Values", "NaN", "If true, invalid values are replaced with NaN instead of dropping the branch.", GH_ParamAccess.item, false);

            // Geometry should be optional so that validation of fields/values works without geometry plugged in
            p[2].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddTextParameter("Fields", "F", "Validated fields.", GH_ParamAccess.tree);
            p.AddGenericParameter("Values", "V", "Validated values.", GH_ParamAccess.tree);
            p.AddGenericParameter("Geometry", "G", "Validated geometry (invalid items removed).", GH_ParamAccess.tree);
            p.AddTextParameter("Message", "Msg", "Summary of dropped branches and cleaned geometry.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var fields = new GH_Structure<GH_String>();
            var values = new GH_Structure<IGH_Goo>();
            var geom = new GH_Structure<IGH_Goo>();
            bool pop_nan_vals = false;

            // Explicit generic calls for clarity and reliability
            if (!DA.GetDataTree<GH_String>(0, out fields)) return;
            if (!DA.GetDataTree<IGH_Goo>(1, out values)) return;

            // Geometry is optional; if not supplied, continue with an empty structure
            bool haveGeom = DA.GetDataTree<IGH_Goo>(2, out geom);

            DA.GetData(3, ref pop_nan_vals);

            var outFields = new GH_Structure<GH_String>();
            var outValues = new GH_Structure<IGH_Goo>();
            var outGeom = new GH_Structure<IGH_Goo>();
            var report = new StringBuilder();
            int droppedBranches = 0;
            int cleanedGeoms = 0;

            // Collect union of all paths, then sort for stable output
            var pathSet = new HashSet<GH_Path>(new GH_PathComparer());
            foreach (var pth in fields.Paths) pathSet.Add(pth);
            foreach (var pth in values.Paths) pathSet.Add(pth);
            if (haveGeom)
                foreach (var pth in geom.Paths) pathSet.Add(pth);
            var allPaths = pathSet.ToList();
            allPaths.Sort((a, b) => a.ToString().CompareTo(b.ToString()));

            foreach (var path in allPaths)
            {
                var reasons = new List<string>();

                bool hasF = fields.PathExists(path);
                bool hasV = values.PathExists(path);
                bool hasG = haveGeom && geom.PathExists(path);

                if (!hasF) reasons.Add("missing fields branch");
                if (!hasV) reasons.Add("missing values branch");
                // Missing geometry is NOT a hard error; only note if geometry input was actually provided
                if (haveGeom && !hasG) reasons.Add("missing geometry branch");

                // Get branches as typed lists
                List<GH_String> fBranch = null;
                List<IGH_Goo> vBranch = null;
                List<IGH_Goo> gBranch = null;

                if (hasF)
                {
                    var ilist = fields.get_Branch(path);
                    if (ilist != null)
                    {
                        fBranch = new List<GH_String>(ilist.Count);
                        foreach (var item in ilist)
                        {
                            var s = item as GH_String;
                            if (s != null) fBranch.Add(s);
                        }
                    }
                }
                if (hasV)
                {
                    var ilist = values.get_Branch(path);
                    if (ilist != null)
                    {
                        vBranch = new List<IGH_Goo>(ilist.Count);
                        foreach (var item in ilist)
                        {
                            var goo = item as IGH_Goo;
                            if (goo != null) vBranch.Add(goo);
                        }
                    }
                }
                if (hasG)
                {
                    var ilist = geom.get_Branch(path);
                    if (ilist != null)
                    {
                        gBranch = new List<IGH_Goo>(ilist.Count);
                        foreach (var item in ilist)
                        {
                            var goo = item as IGH_Goo;
                            if (goo != null) gBranch.Add(goo);
                        }
                    }
                }

                // Basic content checks
                if (hasF && (fBranch == null || fBranch.Count == 0)) reasons.Add("empty fields");
                if (hasV && (vBranch == null || vBranch.Count == 0)) reasons.Add("empty values");

                // Field/value alignment + value validity
                if (hasF && hasV && fBranch != null && vBranch != null)
                {
                    if (fBranch.Count != vBranch.Count)
                    {
                        reasons.Add($"field/value count mismatch ({fBranch.Count} vs {vBranch.Count})");
                    }
                    else
                    {
                        bool foundInvalid = false;
                        for (int i = 0; i < vBranch.Count; i++)
                        {
                            var original = vBranch[i];
                            if (!IsValidValue(original))
                            {
                                foundInvalid = true;
                                if (pop_nan_vals)
                                {
                                    // Replace invalid with NaN stand-in
                                    vBranch[i] = new GH_Number(double.NaN);
                                }
                                else
                                {
                                    reasons.Add($"invalid value at index {i}");
                                    break; // bail if we’re not populating
                                }
                            }
                        }
                        if (foundInvalid && pop_nan_vals)
                        {
                            reasons.Add("invalid values replaced with NaN");
                        }
                    }
                }

                // Geometry filtering: allow many per branch, remove only the bad ones
                var keptGeoms = new List<IGH_Goo>();
                var removedIdx = new List<int>();
                if (hasG && gBranch != null)
                {
                    for (int i = 0; i < gBranch.Count; i++)
                    {
                        var obj = gBranch[i];
                        if (IsValidGeometryKeep(obj))
                        {
                            keptGeoms.Add(obj);
                        }
                        else
                        {
                            removedIdx.Add(i);
                        }
                    }
                    if (removedIdx.Count > 0)
                    {
                        cleanedGeoms += removedIdx.Count;
                        reasons.Add($"removed {removedIdx.Count} invalid geom(s) at index/indices [{string.Join(",", removedIdx)}]");
                    }
                    if (keptGeoms.Count == 0)
                    {
                        reasons.Add("no valid geometry remains");
                    }
                }

                // Decide to keep/drop
                bool hardErrors = reasons.Any(r =>
                    r.StartsWith("missing fields") ||
                    r.StartsWith("missing values") ||
                    r.StartsWith("empty fields") ||
                    r.StartsWith("empty values") ||
                    r.StartsWith("field/value count mismatch") ||
                    r.StartsWith("invalid value") ||
                    r.StartsWith("no valid geometry"));

                if (!hardErrors)
                {
                    if (fBranch != null) outFields.AppendRange(fBranch, path);
                    if (vBranch != null) outValues.AppendRange(vBranch, path);

                    // Append kept geometry if any, but do not require geometry to exist for keeping the branch
                    foreach (var g in keptGeoms) outGeom.Append(g, path);

                    // If we only removed some geoms, keep a note in report (informational)
                    if (removedIdx.Count > 0)
                        report.AppendLine($"{path}: removed {removedIdx.Count} invalid geometry item(s) at [{string.Join(",", removedIdx)}]");

                    // Informational note if geometry input was supplied but the branch is missing
                    if (haveGeom && !hasG)
                        report.AppendLine($"{path}: missing geometry branch");
                }
                else
                {
                    droppedBranches++;
                    // Exclude purely informational "removed" notes from drop reasons
                    report.AppendLine($"{path}: {string.Join("; ", reasons.Where(r => !r.StartsWith("removed ")))}");
                }
            }

            DA.SetDataTree(0, outFields);
            DA.SetDataTree(1, outValues);
            DA.SetDataTree(2, outGeom);

            string msg;
            if (droppedBranches == 0 && cleanedGeoms == 0) msg = "All branches valid. No geometry needed cleaning.";
            else if (droppedBranches == 0) msg = $"All branches kept. Cleaned {cleanedGeoms} invalid geometry item(s):\n" + report.ToString();
            else msg = $"Dropped {droppedBranches} branch(es). Also cleaned {cleanedGeoms} invalid geometry item(s):\n" + report.ToString();

            DA.SetData(3, msg);
        }

        // Value checks: Treat nulls, NaNs, and empty strings as invalid. Accept valid IGH_Goo.
        private static bool IsValidValue(object v)
        {
            if (v == null) return false;

            // Unwrap GH_Goo if present
            var goo = v as IGH_Goo;
            if (goo != null)
            {
                if (!goo.IsValid) return false;
                var sv = goo.ScriptVariable();
                if (sv == null) return true; // accept valid goo even if it doesn't unwrap meaningfully
                v = sv;
            }

            if (v is double d) return !double.IsNaN(d) && !double.IsInfinity(d);
            if (v is float f) return !float.IsNaN(f) && !float.IsInfinity(f);
            if (v is string s) return !string.IsNullOrWhiteSpace(s);
            if (v is bool) return true;
            if (v is int || v is long || v is short || v is byte || v is uint || v is ulong || v is ushort || v is sbyte) return true;

            // Accept other non-null objects by default
            return true;
        }

        // Validate geometry and allow many per branch. Reject invalid or degenerate curves (zero length).
        private static bool IsValidGeometryKeep(object g)
        {
            if (g == null) return false;

            // Unwrap wrappers and IGH_Goo
            var goo = g as IGH_Goo;
            if (goo != null)
            {
                // Prefer IGH_GeometricGoo processing
                var ggeo = goo as IGH_GeometricGoo;
                if (ggeo != null)
                {
                    if (!ggeo.IsValid) return false;
                    var sv = ggeo.ScriptVariable();
                    if (sv is GeometryBase gbUnwrap) return IsValidGeometryBase(gbUnwrap);
                    return true; // valid geometric goo even if not unwrapped
                }

                // Fallback unwrap for generic goo
                var sv2 = goo.ScriptVariable();
                if (sv2 is GeometryBase gb2) return IsValidGeometryBase(gb2);
                return false; // non-geometry goo in geometry input
            }

            if (g is GeometryBase gb)
                return IsValidGeometryBase(gb);

            // Unknown non-geometry object
            return false;
        }

        private static bool IsValidGeometryBase(GeometryBase gb, double tol = 1e-9)
        {
            if (gb == null || !gb.IsValid) return false;

            // Curves: remove zero-length
            if (gb is Curve c)
            {
                double len = c.GetLength();
                if (!Rhino.RhinoMath.IsValidDouble(len)) return false;
                if (len <= tol) return false;

                if (c.TryGetPolyline(out Polyline pl))
                {
                    for (int i = 1; i < pl.Count; i++)
                    {
                        if (pl[i - 1].DistanceTo(pl[i]) <= tol) return false;
                    }
                }
                return true;
            }

            // Other geometry types: rely on IsValid
            return true;
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

        public override Guid ComponentGuid => new Guid("3A2E3C3D-2C36-45B2-9F1D-2F1B4F3E9C9E");
    }
}
