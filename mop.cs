using System;
using System.Reflection;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Xml.Serialization;

using CamBam;
using CamBam.CAD;
using CamBam.CAM;
using CamBam.Geom;
using CamBam.UI;
using CamBam.Values;
using CamBam.Util;

using Matmill;

namespace Trochomops
{
    // toolpath combines flat trajectory and depth
    class Toolpath
    {
        public readonly Sliced_path Trajectory;
        public readonly double Top;
        public readonly double Bottom;
        public Polyline Leadin;
        public Toolpath(Sliced_path path, double bottom, double top)
        {
            this.Trajectory = path;
            this.Bottom = bottom;
            this.Top = top;
        }
    }

    class Traj_metainfo
    {
        public Vector2F Start_normal;
    }

    [Serializable]
    public class Trochomop : MOPFromGeometry
    {
        [NonSerialized]
        private List<Sliced_path> _trajectories = new List<Sliced_path>();
        [NonSerialized]
        private List<Toolpath> _toolpaths = new List<Toolpath>();

        //--- these are for rendering only !
        [NonSerialized]
        private List<Surface> _visual_cut_widths = new List<Surface>();
        [NonSerialized]
        private List<Polyline> _visual_rapids = new List<Polyline>();

        //--- mop properties

        protected CBValue<CutOrderingOption> _cut_ordering;
        protected CBValue<MillingDirectionOptions> _milling_direction;
        protected CBValue<double> _depth_increment;
        protected CBValue<double> _final_depth_increment;
        protected CBValue<double> _stepover;
        protected CBValue<double> _target_depth;
        protected CBValue<LeadMoveInfo> _leadin;
        protected double _chord_feedrate = 0;
        protected double _spiral_feedrate = 0;

        protected bool _should_smooth_chords = false;
        protected bool _should_draw_chords = false;

        //--- hidden base parameters

        [XmlIgnore, Browsable(false)]
        public new CBValue<OptimisationModes> OptimisationMode
        {
            get { return base.OptimisationMode; }
            set { }
        }

        //--- visible parameters which may be styled

        [CBKeyValue, Category("Step Over"), DefaultValue(typeof(CBValue<double>), "Default"), Description("The cut is increased by this amount each step, expressed as a decimal (0-1.0) of the cutter width."), DisplayName("StepOver")]
        public CBValue<double> StepOver
        {
            get { return _stepover; }
            set { _stepover = value; }
        }

        [CBKeyValue, Category("Cutting Depth"), DefaultValue(typeof(CBValue<double>), "Default"), Description("Depth increment of each machining pass."), DisplayName("Depth Increment")]
        public CBValue<double> DepthIncrement
        {
            get { return _depth_increment; }
            set { _depth_increment = value; }
        }

        [Category("Cutting Depth"), DefaultValue(typeof(CBValue<double>), "Default"), Description("The depth increment of the final machining pass."), DisplayName("Final Depth Increment")]
        public CBValue<double> FinalDepthIncrement
        {
            get { return _final_depth_increment; }
            set { _final_depth_increment = value; }
        }

        [Category("Options"), DefaultValue(typeof(CBValue<MillingDirectionOptions>), "Default"), Description("Controls the direction the cutter moves around the toolpath.\r\nConventional or Climb milling supported."), DisplayName("Milling Direction")]
        public CBValue<MillingDirectionOptions> MillingDirection
        {
            get { return _milling_direction; }
            set { _milling_direction = value; }

        }

        [Category("Options"), DefaultValue(typeof(CBValue<CutOrderingOption>), "Default"), Description("Controls whether to cut to depth first or all cuts on this level first."), DisplayName("Cut Ordering")]
        public CBValue<CutOrderingOption> CutOrdering
        {
            get { return _cut_ordering; }
            set { _cut_ordering = value; }
        }

        [CBKeyValue, Category("Cutting Depth"), DefaultValue(typeof(CBValue<double>), "Default"), Description("Final depth of the machining operation."), DisplayName("Target Depth")]
        public CBValue<double> TargetDepth
        {
            get { return _target_depth; }
            set { _target_depth = value; }
        }

        [Category("Lead In/Out"), DefaultValue(typeof(CBValue<LeadMoveInfo>), "Default"), Description("Defines the lead in move as the tool enters the stock."), DisplayName("Lead In Move")]
        public CBValue<LeadMoveInfo> LeadInMove
        {
            get { return _leadin; }
            set { _leadin = value; }
        }



        //--- our own new parameters. No reason to make them CBValues, since they couldn't be styled anyway

        [
            CBKeyValue,
            Category("Feedrates"),
            DefaultValue(0),
            Description("The feed rate to use for the chords and movements inside the milled pocket.  If 0 use cutting feedrate."),
            DisplayName("Chord Feedrate")
        ]
        public double Chord_feedrate
        {
            get { return _chord_feedrate; }
            set { _chord_feedrate = value; }
        }

        [
            CBKeyValue,
            Category("Feedrates"),
            DefaultValue(0),
            Description("The feed rate to use for the spiral opening the pocket. If 0 use cutting feedrate."),
            DisplayName("Spiral Feedrate")
        ]
        public double Spiral_feedrate
        {
            get { return _spiral_feedrate; }
            set { _spiral_feedrate = value; }
        }

        [
            CBAdvancedValue,
            Category("Options"),
            Description("Replace straight chords with the smooth arcs to form a continous toolpath. " +
                        "This may be useful on a machines with the slow acceleration.\n" +
                        "Not applied to the the mixed milling direction."),
            DisplayName("Smooth chords")
        ]
        public bool Should_smooth_chords
        {
            get { return _should_smooth_chords; }
            set { _should_smooth_chords = value; }
        }

        [
            CBAdvancedValue,
            Category("Options"),
            Description("Display the chords. The chords clutters the view, but may be useful for debug."),
            DisplayName("Show chords")
        ]
        public bool Should_draw_chords
        {
            get { return _should_draw_chords; }
            set { _should_draw_chords = value; }
        }

        //-- read-only About field

        [
            XmlIgnore,
            Category("Misc"),
            DisplayName("Plugin Version"),
            Description("https://github.com/jkmnt/matmill\njkmnt at git@firewood.fastmail.com\n\ntriangulation code: triangle.net (https://triangle.codeplex.com)")
        ]
        public string Version
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version.ToString(); }
        }

        public override bool NeedsRebuild
        {
            get { return _trajectories == null || _trajectories.Count == 0; }
        }

        private double[] get_z_layers()
        {
            return base.GetZLayers(base.StockSurface.Cached, _target_depth.Cached, _depth_increment.Cached, _final_depth_increment.Cached);
        }

        protected bool is_inch_units()
        {
            return base._CADFile != null && base._CADFile.DrawingUnits == Units.Inches;
        }

        private List<Toolpath> gen_ordered_toolpath(List<Sliced_path> trajectories, double[] bottoms)
        {
            List<Toolpath> toolpaths = new List<Toolpath>();

            // cut ordering == level first is meaningful only for a several pockets
            if (trajectories.Count < 2 || _cut_ordering.Cached == CutOrderingOption.DepthFirst)
            {
                foreach (Sliced_path traj in trajectories)
                {
                    double surface = base.StockSurface.Cached;
                    foreach (double bot in bottoms)
                    {
                        toolpaths.Add(new Toolpath(traj, bot, surface));
                        surface = bot;
                    }
                }
            }
            else
            {
                double surface = base.StockSurface.Cached;

                foreach (double bot in bottoms)
                {
                    foreach (Sliced_path traj in trajectories)
                        toolpaths.Add(new Toolpath(traj, bot, surface));
                    surface = bot;
                }
            }

            // now insert leadins
            if (_leadin.Cached != null && _leadin.Cached.LeadInType != LeadInTypeOptions.None)
            {
                foreach (Toolpath tp in toolpaths)
                    tp.Leadin = gen_leadin(tp);
            }

            return toolpaths;
        }

        private Polyline gen_leadin(Toolpath path)
        {
            Sliced_path_item spiral = path.Trajectory[0];

            if (spiral.Item_type != Sliced_path_item_type.SPIRAL)
                throw new Exception("no spiral in sliced path");

            Vector2F start_normal = Vector2F.Undefined;

            if (path.Trajectory.Extension != null && path.Trajectory.Extension is Traj_metainfo)
            {
                Traj_metainfo meta = (Traj_metainfo) path.Trajectory.Extension;
                start_normal = meta.Start_normal;
            }

            LeadMoveInfo move = _leadin.Cached;
            Polyline p = move.GetLeadInToolPath(spiral,
                                       spiral.Direction,
                                       start_normal,
                                       base.PlungeFeedrate.Cached,
                                       base.CutFeedrate.Cached,
                                       base.StockSurface.Cached,
                                       _depth_increment.Cached,
                                       path.Bottom,
                                       move.ValidSpiralAngle, path.Top - path.Bottom);

            return p;
        }

        protected void redraw_parameters()
        {
            CamBamUI.MainUI.ObjectProperties.Refresh();
        }

        private Surface polyline_to_surface(Polyline p, double z)
        {
            PolylineToMesh mesh = new PolylineToMesh(p);
            Surface surface = mesh.ToWideLine(base.ToolDiameter.Cached);

            Matrix4x4F mx = Matrix4x4F.Translation(0.0, 0.0, z - 0.001);
            if (Transform.Cached != null)
                mx *= Transform.Cached;

            surface.ApplyTransformation(mx);
            return surface;
        }

        private List<Surface> calc_visual_cut_widths(List<Toolpath> toolpaths, double first_bottom, double last_bottom)
        {
            List<Surface> surfaces = new List<Surface>();

            foreach (Toolpath path in toolpaths)
            {
                // show lead-ins for the first depth level
                if (path.Bottom == first_bottom && path.Leadin != null)
                    surfaces.Add(polyline_to_surface(path.Leadin, path.Bottom));

                // show cut traces for the last depth level
                if (path.Bottom == last_bottom)
                {
                    foreach (Sliced_path_item item in path.Trajectory)
                    {
                        if (item.Item_type == Sliced_path_item_type.SLICE || item.Item_type == Sliced_path_item_type.SPIRAL)
                            surfaces.Add(polyline_to_surface(item, path.Bottom));
                    }
                }
            }

            return surfaces;
        }

        private List<Polyline> calc_visual_rapids(List<Toolpath> toolpaths)
        {
            List<Polyline> rapids = new List<Polyline>();

            double thres = base.GetDistanceThreshold();

            Point3F lastpt = Point3F.Undefined;

            // rapids are possible only between depth levels of pocket and separate pockets
            foreach (Toolpath path in toolpaths)
            {
                if (! lastpt.IsUndefined)
                {
                    Point3F to;

                    if (path.Leadin != null)
                        to = path.Leadin.FirstPoint;
                    else
                        to = (Point3F)path.Trajectory[0].FirstPoint;

                    double dist = Point2F.Distance((Point2F)lastpt, (Point2F)to);

                    if (dist > thres + (double)CamBamConfig.Defaults.GeneralTolerance)
                    {
                        // rapid here from last to first point of pocket
                        Polyline p = new Polyline();
                        p.Add(lastpt);
                        p.Add(new Point3F(lastpt.X, lastpt.Y, ClearancePlane.Cached));
                        p.Add(new Point3F(to.X, to.Y, ClearancePlane.Cached));
                        p.Add(new Point3F(to.X, to.Y, path.Bottom + to.Z));
                        rapids.Add(p);
                    }
                }

                lastpt = path.Trajectory[path.Trajectory.Count - 1].LastPoint;
                lastpt = new Point3F(lastpt.X, lastpt.Y, path.Bottom);
            }

            return rapids;
        }

        private void print_toolpath_stats(List<Toolpath> toolpaths, List<Polyline> rapids)
        {
            double leadins_len = 0;
            double spirals_len = 0;
            double slices_len = 0;
            double moves_len = 0;
            double rapids_len = 0;

            // collect cut lengths
            foreach (Toolpath path in toolpaths)
            {
                if (path.Leadin != null)
                    leadins_len += path.Leadin.GetPerimeter();

                foreach (Sliced_path_item item in path.Trajectory)
                {
                    double len = item.GetPerimeter();

                    switch (item.Item_type)
                    {
                    case Sliced_path_item_type.SPIRAL:
                        spirals_len += len;
                        break;

                    case Sliced_path_item_type.SLICE:
                        slices_len += len;
                        break;

                    case Sliced_path_item_type.CHORD:
                    case Sliced_path_item_type.SMOOTH_CHORD:
                    case Sliced_path_item_type.GUIDE:
                    case Sliced_path_item_type.SLICE_SHORTCUT:
                        moves_len += len;
                        break;
                    }
                }
            }

            // collect rapids lengths
            foreach (Polyline p in rapids)
            {
                rapids_len += p.GetPerimeter();
            }

            double cut_len = leadins_len + spirals_len + slices_len + moves_len;

            Logger.log(2, TextTranslation.Translate("Toolpath distance '{0}' : {1} + rapids : {2} = total : {3}"),
                                                  base.Name,
                                                  cut_len,
                                                  rapids_len,
                                                  cut_len + rapids_len);

            // calculate feedrates
            double normal_feedrate = base.CutFeedrate.Cached;
            if (normal_feedrate <= 0)
                return;

            double chord_feedrate = _chord_feedrate != 0 ? _chord_feedrate : normal_feedrate;
            double spiral_feedrate = _spiral_feedrate != 0 ? _spiral_feedrate : normal_feedrate;
            double leadin_feedrate = _leadin.Cached != null && _leadin.Cached.LeadInFeedrate != 0 ? _leadin.Cached.LeadInFeedrate : normal_feedrate;
            double rapid_feedrate = 600;    // something big

            double cut_time = 0;
            cut_time += leadins_len / leadin_feedrate;
            cut_time += spirals_len / spiral_feedrate;
            cut_time += slices_len / normal_feedrate;
            cut_time += moves_len / chord_feedrate;

            double rapid_time = rapids_len / rapid_feedrate;

            TimeSpan cut_dur = new TimeSpan(0, 0, (int)(cut_time * 60.0));
            TimeSpan rapids_dur = new TimeSpan(0, 0, (int)(rapid_time * 60.0));


            Logger.log(2, TextTranslation.Translate("Estimated Toolpath '{0}' duration : {1} + rapids : {2} = total : {3}"),
                                                  base.Name,
                                                  cut_dur,
                                                  rapids_dur,
                                                  cut_dur + rapids_dur);
        }

        internal void reset_toolpaths()
        {
            _trajectories.Clear();
            _toolpaths.Clear();
            _visual_cut_widths.Clear();
            _visual_rapids.Clear();
            GC.Collect();
        }

        internal void insert_toolpaths(List<Sliced_path> trajectories)
        {
            _trajectories = trajectories;
            double[] bottoms = get_z_layers();
            _toolpaths = gen_ordered_toolpath(_trajectories, bottoms);
            _visual_cut_widths = calc_visual_cut_widths(_toolpaths, bottoms[0], bottoms[bottoms.Length - 1]);
            _visual_rapids = calc_visual_rapids(_toolpaths);

            // XXX: transforms are not accounted for in stats calc or may print wrong results
            print_toolpath_stats(_toolpaths, _visual_rapids);
        }

        private void emit_toolpath(MachineOpToGCode gcg, Toolpath path)
        {
            // first item is the spiral by convention
            if (path.Trajectory[0].Item_type != Sliced_path_item_type.SPIRAL)
                throw new Exception("no spiral in sliced path");

            CBValue<double> normal_feedrate = base.CutFeedrate;
            CBValue<double> chord_feedrate = _chord_feedrate != 0 ? new CBValue<double>(_chord_feedrate) : base.CutFeedrate;
            CBValue<double> spiral_feedrate = _spiral_feedrate != 0 ? new CBValue<double>(_spiral_feedrate) : base.CutFeedrate;
            CBValue<double> leadin_feedrate = _leadin.Cached != null && _leadin.Cached.LeadInFeedrate != 0 ? new CBValue<double>(_leadin.Cached.LeadInFeedrate) : base.CutFeedrate;

            if (path.Leadin != null)
            {
                base.CutFeedrate = leadin_feedrate;
                Polyline p = (Polyline)path.Leadin.Clone();
                p.ApplyTransformation(Matrix4x4F.Translation(0, 0, path.Bottom));
                gcg.AppendPolyLine(p, double.NaN);
            }

            foreach (Sliced_path_item item in path.Trajectory)
            {
                switch (item.Item_type)
                {
                case Sliced_path_item_type.SPIRAL:
                    base.CutFeedrate = spiral_feedrate;
                    break;

                case Sliced_path_item_type.SLICE:
                    base.CutFeedrate = normal_feedrate;
                    break;

                case Sliced_path_item_type.CHORD:
                case Sliced_path_item_type.SMOOTH_CHORD:
                case Sliced_path_item_type.SLICE_SHORTCUT:
                case Sliced_path_item_type.GUIDE:
                    base.CutFeedrate = chord_feedrate;
                    break;

                default:
                    throw new Exception("unknown item type in sliced trajectory");
                }

                Polyline p = (Polyline)item.Clone();
                p.ApplyTransformation(Matrix4x4F.Translation(0, 0, path.Bottom));
                gcg.AppendPolyLine(p, double.NaN);
            }

            base.CutFeedrate = normal_feedrate;
        }


        public override List<Polyline> GetOutlines()
        {
            List<Polyline> outlines = new List<Polyline>();

            foreach (Toolpath path in _toolpaths)
            {
                Matrix4x4F mx = Matrix4x4F.Translation(0.0, 0.0, path.Bottom);
                if (Transform.Cached != null)
                    mx *= Transform.Cached;

                foreach (Sliced_path_item p in path.Trajectory)
                {
                    if (p.Item_type != Sliced_path_item_type.SLICE && p.Item_type != Sliced_path_item_type.SPIRAL)
                        continue;

                    Polyline poly = (Polyline) p.Clone();
                    poly.ApplyTransformation(mx);
                    outlines.Add(poly);
                }
            }

            return outlines;
        }

        public override List<Surface> GetCutWidths()
        {
            return _visual_cut_widths;
        }

        public override Point3F GetInitialCutPoint()
        {
            if (_toolpaths.Count == 0)
                return Point3F.Undefined;

            Toolpath tp0 = _toolpaths[0];
            Point3F pt;
            if (tp0.Leadin != null)
            {
                pt = tp0.Leadin.FirstPoint;
            }
            else
            {
                if (tp0.Trajectory.Count == 0)
                    return Point3F.Undefined;

                Sliced_path_item ppi = tp0.Trajectory[0];
                if (ppi.Points.Count == 0)
                    return Point3F.Undefined;

                pt = ppi.Points[0].Point;
                pt = new Point3F(pt.X, pt.Y, tp0.Bottom);
            }

            if (Transform.Cached != null)
                pt *= Transform.Cached;

            return pt;
        }

        private void paint_toolpath(ICADView iv, Display3D d3d, Color arccolor, Color linecolor, Toolpath path)
        {
            Polyline leadin = path.Leadin;

            Color chord_color = Color.FromArgb(128, CamBamConfig.Defaults.ToolpathRapidColor);

            Matrix4x4F mx = Matrix4x4F.Translation(0.0, 0.0, path.Bottom);
            if (Transform.Cached != null)
                mx *= Transform.Cached;

            if (leadin != null)
            {
                d3d.ModelTransform = mx;
                d3d.LineWidth = 1F;
                leadin.Paint(d3d, arccolor, linecolor);
                base.PaintDirectionVector(iv, leadin, d3d, mx);
            }

            foreach (Sliced_path_item p in path.Trajectory)
            {
                Color acol = arccolor;
                Color lcol = linecolor;

                if (p.Item_type == Sliced_path_item_type.CHORD || p.Item_type == Sliced_path_item_type.SMOOTH_CHORD || p.Item_type == Sliced_path_item_type.SLICE_SHORTCUT)
                {
                    if (! _should_draw_chords)
                        continue;
                    acol = chord_color;
                    lcol = chord_color;
                }

                if (p.Item_type == Sliced_path_item_type.DEBUG_MEDIAL_AXIS)
                {
                    acol = Color.Cyan;
                    lcol = Color.Cyan;
                }

                d3d.ModelTransform = mx;
                d3d.LineWidth = 1F;

                p.Paint(d3d, acol, lcol);
                base.PaintDirectionVector(iv, p, d3d, mx);
            }
        }

        private void paint_cut_widths(Display3D d3d)
        {
            d3d.LineColor = CamBamConfig.Defaults.CutWidthColor;
            d3d.LineStyle = LineStyle.Solid;
            d3d.LineWidth = 0F;
            d3d.ModelTransform = Matrix4x4F.Identity;
            d3d.UseLighting = false;
            foreach (Surface s in _visual_cut_widths)
                s.Paint(d3d);

            d3d.UseLighting = true;
        }

        private void paint_rapids(Display3D d3d)
        {
            d3d.LineWidth = 1f;
            d3d.LineColor = CamBamConfig.Defaults.ToolpathRapidColor;
            d3d.ModelTransform = Matrix4x4F.Identity;
            d3d.LineStyle = LineStyle.Dotted;

            foreach (Polyline p in _visual_rapids)
                p.Paint(d3d);

            d3d.LineStyle = LineStyle.Solid;
        }

        public override void PostProcess(MachineOpToGCode gcg)
        {
            gcg.DefaultStockHeight = base.StockSurface.Cached;

            if (_trajectories.Count == 0)
                return;

            CBValue<double> original_feedrate = base.CutFeedrate;

            // NOTE: toolpaths are emit in a hacky way to allow variable feedrate.
            // CutFeedrate base setting is patched before posting each toolpath item,
            // and should be restored in the end
            try
            {
                foreach(Toolpath path in _toolpaths)
                    emit_toolpath(gcg, path);
            }
            finally
            {
                base.CutFeedrate = original_feedrate;
            }
        }

        public override void Paint(ICADView iv, Display3D d3d, Color arccolor, Color linecolor, bool selected)
        {
            // XXX: what this line for ?
            base._CADFile = iv.CADFile;

            if (selected)
                base.PaintStartPoint(iv, d3d);

            if (_trajectories.Count == 0) return;

            foreach (Toolpath item in _toolpaths)
                paint_toolpath(iv, d3d, arccolor, linecolor, item);

            if (base._CADFile.ShowCutWidths)
                paint_cut_widths(d3d);

            if (base._CADFile.ShowRapids)
                paint_rapids(d3d);
        }

        public Trochomop(Trochomop src) : base(src)
        {
            CutOrdering = src.CutOrdering;
            MillingDirection = src.MillingDirection;
            DepthIncrement = src.DepthIncrement;
            FinalDepthIncrement = src.FinalDepthIncrement;
            StepOver = src.StepOver;
            TargetDepth = src.TargetDepth;
            LeadInMove = src.LeadInMove;

            Chord_feedrate = src.Chord_feedrate;
            Spiral_feedrate = src.Spiral_feedrate;
            Should_smooth_chords = src.Should_smooth_chords;
            Should_draw_chords = src.Should_draw_chords;
        }

        public Trochomop()
        {
        }

        public Trochomop(CADFile CADFile, ICollection<Entity> plist) : base(CADFile, plist)
        {
        }
    }
}