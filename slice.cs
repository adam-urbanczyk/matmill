using System;
using System.Collections.Generic;

using CamBam.Geom;
using Geom;

// FYI: TED stands for Tool Engagement Depth (or Distance).
// TODO: define it

namespace Matmill
{
    enum Slice_placement
    {
        NORMAL,
        TOO_FAR,
        INSIDE_ANOTHER,
    }

    class Slice
    {
        private readonly Circle2F _ball;
        private List<Arc2F> _segments = new List<Arc2F>();
        private readonly Slice _parent;
        private readonly Slice_placement _placement;
        private double _entry_ted = 0;
        private double _mid_ted = 0;
        private double _max_ted = 0;

        public List<Point2F> Guide;

        public Circle2F Ball                { get { return _ball; } }
        public List<Arc2F> Segments         { get { return _segments; } }
        public Point2F Center               { get { return _ball.Center; } }
        public Point2F Start                { get { return _segments[0].P1; } }
        public Point2F End                  { get { return _segments[1].P2; } }
        public RotationDirection Dir        { get { return _segments[0].Direction; } }
        public Slice Parent                 { get { return _parent; } }
        public Slice_placement Placement    { get { return _placement; } }
        public double Max_ted               { get { return _max_ted; } }
        public double Radius                { get { return _ball.Radius; } }

        static private double angle_between_vectors(Vector2d v0, Vector2d v1, RotationDirection dir)
        {
            double angle = v1.CCW_angle_from(v0);
            return (dir == RotationDirection.CCW) ? angle : (2.0 * Math.PI - angle);
        }

        private double calc_ted(Point2F tool_center, double tool_r)
        {
            Circle2F parent_wall = new Circle2F(_parent.Center, _parent.Radius + tool_r);

            // find the points tool touching parent wall
            Circle2F cut_circle = new Circle2F(tool_center, tool_r);
            Line2F insects = cut_circle.CircleIntersect(parent_wall);

            if (insects.p1.IsUndefined || insects.p2.IsUndefined)
            {
                Logger.err("no wall intersections #0");
                return 0;
            }

            // we're interested in tool head point, so choose more advanced point in mill direction

            Vector2d v1 = new Vector2d(_parent.Center, insects.p1);
            Vector2d v_parent_to_tool_center = new Vector2d(_parent.Center, tool_center);
            Point2F cut_head = v_parent_to_tool_center.Det(v1) * (int)this.Dir > 0 ? insects.p1 : insects.p2;

            // project headpoint to the center find TED
            Vector2d v_me_to_tool_center = new Vector2d(this.Center, tool_center);
            Vector2d v_me_to_cut_head = new Vector2d(this.Center, cut_head);

            double ted = this.Radius + tool_r - v_me_to_cut_head * v_me_to_tool_center.Unit();
            return ted > 0 ? ted : 0;
        }

        private double calc_entry_ted(double tool_r)
        {
            // expand both slices to form walls
            Circle2F parent_wall = new Circle2F(_parent.Center, _parent.Radius + tool_r);
            Circle2F this_wall = new Circle2F(this.Center, this.Radius + tool_r);

            // find the point of cut start (intersection of walls)
            Line2F wall_insects = parent_wall.CircleIntersect(this_wall);

            if (wall_insects.p1.IsUndefined || wall_insects.p2.IsUndefined)
            {
                Logger.err("no wall intersections for entry ted #1");
                return 0;
            }

            Vector2d v1 = new Vector2d(_parent.Center, wall_insects.p1);
            Vector2d v_move = new Vector2d(_parent.Center, this.Center);
            Point2F cut_tail = v_move.Det(v1) * (int)this.Dir < 0 ? wall_insects.p1 : wall_insects.p2;
            Vector2d v_tail = new Vector2d(this.Center, cut_tail);

            Point2F entry_tool_center = this.Center + (v_tail.Unit() * this.Radius).Point;
            return calc_ted(entry_tool_center, tool_r);
        }

        public void Get_extrema(ref Point2F min, ref Point2F max)
        {
            Point2F seg0_min = Point2F.Undefined;
            Point2F seg0_max = Point2F.Undefined;
            Point2F seg1_min = Point2F.Undefined;
            Point2F seg1_max = Point2F.Undefined;

            _segments[0].GetExtrema(ref seg0_min, ref seg0_max);
            _segments[1].GetExtrema(ref seg1_min, ref seg1_max);

            min = new Point2F(Math.Min(seg0_min.X, seg1_min.X), Math.Min(seg0_min.Y, seg1_min.Y));
            max = new Point2F(Math.Max(seg0_max.X, seg1_max.X), Math.Max(seg0_max.Y, seg1_max.Y));
        }

        public void Refine_new(List<Slice> colliding_slices, double end_clearance, double tool_r)
        {
            double clearance = end_clearance;

            // check if arc is small. refining is worthless in this case
            // criterion for smallness: there should be at least 4 segments with chord = clearance, plus
            // one segment to space ends far enough. A pentagon with a 5 segments with edge length = clearance
            // will define the min radius of circumscribed circle. clearance = 2 * R * sin (Pi / 5),
            // R = clearance / 2 / sin (Pi / 5)

            if (_segments[0].P2.DistanceTo(_segments[1].P1) != 0.0)
                throw new Exception("attempt to refine already refined slice");

            double r_min = clearance / 2 / Math.Sin(Math.PI / 5.0);
            if (this.Radius <= r_min)
                return;

            if (colliding_slices.Contains(this))
                throw new Exception("attempt to collide slice with itself");

            Line2F c1_insects = _segments[0].CircleIntersect(new Circle2F(this.Start, clearance));
            Line2F c2_insects = _segments[1].CircleIntersect(new Circle2F(this.End, clearance));
            Point2F c1 = c1_insects.p1.IsUndefined ? c1_insects.p2 : c1_insects.p1;
            Point2F c2 = c2_insects.p1.IsUndefined ? c2_insects.p2 : c2_insects.p1;

            Arc2F seg0 = _segments[0];

            // trim seg0
            foreach (Slice s in colliding_slices)
            {
                if (s == _parent)
                    continue;  // no reason to process it

                Line2F secant = seg0.CircleIntersect(s.Ball);

                // just for now - ignore the case of fully inside
                if (secant.p1.IsUndefined && secant.p2.IsUndefined)
                    continue;

                // two intersections - ignore
                if (! (secant.p1.IsUndefined || secant.p2.IsUndefined))
                    continue;

                // single intersection
                Point2F splitpt = secant.p1.IsUndefined ? secant.p2 : secant.p1;
                if (seg0.P1.DistanceTo(s.Center) < seg0.P2.DistanceTo(s.Center))
                {
//                  if (splitpt.DistanceTo(seg0.P1) < clearance)
                        continue;  // nothing to remove
                }
                else
                {
                    if (splitpt.DistanceTo(seg0.P1) < clearance)
                        splitpt = c1;
                }

                seg0 = new Arc2F(this.Center, this.Start, splitpt, this.Dir);
            }

            Arc2F seg1 = _segments[1];

            // trim seg1
            foreach (Slice s in colliding_slices)
            {
                if (s == _parent)
                    continue;  // no reason to process it

                Line2F secant = seg1.CircleIntersect(s.Ball);

                // just for now - ignore the case of fully inside
                if (secant.p1.IsUndefined && secant.p2.IsUndefined)
                    continue;

                // two intersections - ignore
                if (! (secant.p1.IsUndefined || secant.p2.IsUndefined))
                    continue;

                // single intersection
                Point2F splitpt = secant.p1.IsUndefined ? secant.p2 : secant.p1;
                if (seg1.P2.DistanceTo(s.Center) < seg1.P1.DistanceTo(s.Center))
                {
//                  if (splitpt.DistanceTo(seg1.P2) < clearance)
                    continue;  // nothing to remove
                }
                else
                {
                    if (splitpt.DistanceTo(seg1.P2) < clearance)
                        splitpt = c2;
                }

                seg1 = new Arc2F(this.Center, splitpt, this.End, this.Dir);
            }

            if (seg0.P2.DistanceTo(seg1.P1) < clearance * 2) // segment is too short, ignore it
                return;

            _segments.Clear();
            _segments.Add(seg0);
            _segments.Add(seg1);
        }


        public void Refine(List<Slice> colliding_slices, double end_clearance, double tool_r)
        {
            double clearance = end_clearance;

            // check if arc is small. refining is worthless in this case
            // criterion for smallness: there should be at least 4 segments with chord = clearance, plus
            // one segment to space ends far enough. A pentagon with a 5 segments with edge length = clearance
            // will define the min radius of circumscribed circle. clearance = 2 * R * sin (Pi / 5),
            // R = clearance / 2 / sin (Pi / 5)

            if (_segments[0].P2.DistanceTo(_segments[1].P1) != 0.0)
                throw new Exception("attempt to refine already refined slice");

            double r_min = clearance / 2 / Math.Sin(Math.PI / 5.0);
            if (this.Radius <= r_min)
                return;

            if (colliding_slices.Contains(this))
                throw new Exception("attempt to collide slice with itself");

            // now apply the colliding slices. to keep things simple and robust, we apply just one slice - the one who trims
            // us most (removed length of arc is greatest).

            // end clearance adjustment:
            // to guarantee the tool will never hit the unmilled area while rapiding between segments,
            // arc will always have original ends, trimming will happen in the middle only.
            // to prevent the tool from milling extra small end segments and minimize numeric errors at small tangents,
            // original ends would always stick at least for a clearance (chordal) length.
            // i.e. if the point of intersection of arc and colliding circle is closer than clearance to the end,
            // it is moved to clearance distance.

            // there is a two cases of intersecting circles: with single intersection and with a double intersection.
            // double intersections splits arc to three pieces (length of the middle part is the measure),
            // single intesections splits arc in two (the part inside the circle is removed, its length is the measure).
            // in both cases the intersection points are subject to "end clearance adjustment".
            // single intersections are transformed to the double intersections, second point being one of the end clearances.

            // TODO: calculate clearance point the right way, with math :-)
            Line2F c1_insects = _segments[0].CircleIntersect(new Circle2F(this.Start, clearance));
            Line2F c2_insects = _segments[1].CircleIntersect(new Circle2F(this.End, clearance));
            Point2F c1 = c1_insects.p1.IsUndefined ? c1_insects.p2 : c1_insects.p1;
            Point2F c2 = c2_insects.p1.IsUndefined ? c2_insects.p2 : c2_insects.p1;

            Vector2d v_c1 = new Vector2d(this.Center, c1);
            Vector2d v_c2 = new Vector2d(this.Center, c2);

            Line2F max_secant = new Line2F();
            double max_sweep = 0;

            Arc2F safe_arc = new Arc2F(this.Center, c1, c2, this.Dir);

            foreach (Slice s in colliding_slices)
            {
                if (s == _parent)
                    continue;  // no reason to process it

                Line2F secant = this.Ball.CircleIntersect(s.Ball);

                // only double intersections are of interest
                if (secant.p1.IsUndefined || secant.p2.IsUndefined)
                    continue;

                Vector2d v_ins1 = new Vector2d(this.Center, secant.p1);
                Vector2d v_ins2 = new Vector2d(this.Center, secant.p2);

                if (angle_between_vectors(v_c1, v_ins1, this.Dir) > angle_between_vectors(v_c1, v_c2, this.Dir))
                {
                    secant.p1 = c1.DistanceTo(s.Center) < c2.DistanceTo(s.Center) ? c1 : c2;
                    v_ins1 = new Vector2d(this.Center, secant.p1);
                }

                if (angle_between_vectors(v_c1, v_ins2, this.Dir) > angle_between_vectors(v_c1, v_c2, this.Dir))
                {
                    secant.p2 = c1.DistanceTo(s.Center) < c2.DistanceTo(s.Center) ? c1 : c2;
                    v_ins2 = new Vector2d(this.Center, secant.p2);
                }

                if (secant.p1.DistanceTo(secant.p2) < clearance * 2) // segment is too short, ignore it
                    continue;

                double sweep = angle_between_vectors(v_ins1, v_ins2, this.Dir);

                // sort insects by sweep (already sorted for single, may be unsorted for the double)
                // NOTE: to keep the good margin for numerical errors, we're sorting from original start point, not safe_arc start
                Vector2d v_p1 = new Vector2d(this.Center, this.Start);
                if (angle_between_vectors(v_p1, v_ins1, this.Dir) > angle_between_vectors(v_p1, v_ins2, this.Dir))
                {
                    secant = new Line2F(secant.p2, secant.p1);
                    sweep = 2.0 * Math.PI - sweep;
                }

                if (sweep > max_sweep)
                {
                    // ok, a last check - removed arc midpoint should be inside the colliding circle
                    Arc2F check_arc = new Arc2F(this.Center, secant.p1, secant.p2, this.Dir);
                    if (check_arc.Midpoint.DistanceTo(s.Center) < s.Radius)
                    {
                        max_sweep = sweep;
                        max_secant = secant;
                    }
                    else
                    {
                        ;
                    }
                }
            }

            if (max_sweep == 0)
                return;

            Arc2F head = new Arc2F(this.Center, this.Start, max_secant.p1, this.Dir);
            Arc2F tail = new Arc2F(this.Center, max_secant.p2, this.End, this.Dir);

            _segments.Clear();
            _segments.Add(head);
            _segments.Add(tail);

            return;

            // recalculate max TED accounting for the new endpoints.
            // this TED is 'virtual' and averaged with previous max_ted to make arcs look more pretty

            // if ends of removed segment are at the same side of direction vector,
            // midpoint is still present, _max_ted is valid and is maximum for sure
            Vector2d v_move = new Vector2d(_parent.Center, this.Center);
            Vector2d v_removed_p1 = new Vector2d(_parent.Center, max_secant.p1);
            Vector2d v_removed_p2 = new Vector2d(_parent.Center, max_secant.p2);

            if (v_move.Det(v_removed_p1) * v_move.Det(v_removed_p2) > 0) return;

            double ted_head_end = calc_ted(max_secant.p1, tool_r);
            double ted_tail_start = calc_ted(max_secant.p2, tool_r);

            double ted = Math.Max(ted_head_end, ted_tail_start);

            if (ted <= 0)
            {
                Logger.err("max TED vanished after refining the slice !");
                return;
            }

            ted = (ted + _mid_ted) / 2;
            _max_ted = Math.Max(ted, _entry_ted);
        }

        public void Append_leadin_and_leadout(double leadin_angle, double leadout_angle)
        {
            // TODO: since we got a two segments, we could analyze collapse in a better way
            return;

            RotationDirection dir = Dir;
            Point2F start = Start;
            Point2F end = End;
            Point2F center = Center;

            Vector2d old_v1 = new Vector2d(center, start);
            Vector2d old_v2 = new Vector2d(center, end);

            Vector2d v1 = old_v1.Rotated(-(int)dir * leadin_angle);
            Vector2d v2 = old_v2.Rotated((int)dir * leadout_angle);

            double old_det = old_v1.Det(old_v2);
            double new_det = v1.Det(v2);

            // can't apply since slice will collapse
            if (Math.Sign(old_det) != Math.Sign(new_det))
                return;

            _segments[0] = new Arc2F(center, center + v1.Point, _segments[0].P2, dir);
            _segments[1] = new Arc2F(center, _segments[1].P1, center + v2.Point, dir);
        }


        private void create_arc_circle(Point2F p1, RotationDirection dir)
        {
            double seg_sweep = dir == RotationDirection.CW ? -2 * Math.PI / 3 : 2 * Math.PI / 3;

            Point2F p2 = this.Center - (Point2F)new Vector2d(this.Center, p1);

            _segments.Clear();
            _segments.Add(new Arc2F(this.Center, p1, p2, dir));
            _segments.Add(new Arc2F(this.Center, p2, p1, dir));
        }

        public void Change_startpoint(Point2F p1)
        {
            if (_parent != null)
                throw new Exception("startpoint may be changed for the root slice only");
            if (Math.Abs((p1.DistanceTo(this.Center) - this.Radius) / this.Radius) > 0.001)
                throw new Exception("new startpoint is outside the initial circle");

            create_arc_circle(p1, Dir);
        }

        public Slice(Slice parent, Point2F center, double radius, RotationDirection dir, double tool_r, Point2F magnet)
        {
            _parent = parent;
            _ball = new Circle2F(center, radius);

            double dist = Point2F.Distance(center, parent.Center);
            double delta_r = this.Radius - parent.Radius;
            double coarse_ted = dist + delta_r;

            // possible placements for the slice:
            // 1) too far from parent and do not intersect - overshoot
            // 2) far from parent and intersect in a single intermediate point - overshoot
            // 3) inside the parent and intersect in a single point - undershoot
            // 4) contains the parent and intersect in a single point - ok, but handle with care
            // 5) inside the parent and do not intersect - undershoot
            // 6) contains the parent and do not intersect - undershoot (?)
            // 7) completely matches the parent and intersect in infinite number of points - undershoot (?)
            // 8) intersects the parent in two points - ok

            // case 7
            if (dist == 0 && delta_r == 0)
            {
                _placement = Slice_placement.INSIDE_ANOTHER;
                return;
            }
            // case 1 (and case 2 if calculation is unlikely precise)
            if (dist >= this.Radius + parent.Radius)
            {
                _placement = Slice_placement.TOO_FAR;
                return;
            }
            // discard the slices with TED >= tool diameter early
            // these slices are not valid and check prevents fails during the calculation of real angle-based TED
            // this should cover case 2 too
            if (coarse_ted > tool_r * 1.999)
            {
                _placement = Slice_placement.TOO_FAR;
                return;
            }
            // now we either have no intersections (cases 5, 6), one intersection (cases 3, 4), two intersections (case 8)
            Line2F insects = _parent.Ball.CircleIntersect(this._ball);
            // cases 5, 6
            if (insects.p1.IsUndefined && insects.p2.IsUndefined)
            {
                _placement = Slice_placement.INSIDE_ANOTHER;
                return;
            }
            // cases 3, 4
            if (insects.p1.IsUndefined || insects.p2.IsUndefined)
            {
                // case 3
                if (this.Radius <= parent.Radius)
                {
                    _placement = Slice_placement.INSIDE_ANOTHER;
                    return;
                }
                // case 4
                _placement = Slice_placement.NORMAL;

                if (insects.p1.IsUndefined)
                    insects.p1 = insects.p2;
                else
                    insects.p2 = insects.p1;
            }
            else
            {
                // case 8
                _placement = Slice_placement.NORMAL;
            }

            Vector2d v_move = new Vector2d(_parent.Center, this.Center);
            Vector2d v1 = new Vector2d(_parent.Center, insects.p1);
            RotationDirection default_dir = v_move.Det(v1) > 0 ? RotationDirection.CW : RotationDirection.CCW;

            if (dir == RotationDirection.Unknown)
            {
                if (magnet.IsUndefined)
                {
                    dir = RotationDirection.CCW;    // just something
                }
                else
                {
                    if (insects.p1.DistanceTo(magnet) < insects.p2.DistanceTo(magnet))  // match, use existing dir
                        dir = default_dir;
                    else
                        dir = (default_dir == RotationDirection.CCW) ? RotationDirection.CW : RotationDirection.CCW;   // flip
                }
            }

            Point2F midpoint = this.Center + (v_move.Unit() * this.Radius).Point;

            if (default_dir == dir)
            {
                _segments.Add(new Arc2F(this.Center, insects.p1, midpoint, dir));
                _segments.Add(new Arc2F(this.Center, midpoint, insects.p2, dir));
            }
            else
            {
                _segments.Add(new Arc2F(this.Center, insects.p2, midpoint, dir));
                _segments.Add(new Arc2F(this.Center, midpoint, insects.p1, dir));
            }

            _mid_ted  = calc_ted(midpoint, tool_r);

            if (_mid_ted <= 0)
            {
                Logger.warn("forced to patch mid_ted");
                _mid_ted = coarse_ted;    // shouldn't be, but ok
            }

            _entry_ted = calc_entry_ted(tool_r);

            _max_ted = Math.Max(_mid_ted, _entry_ted);
        }

        public Slice(Point2F center, double radius, RotationDirection dir)
        {
            _ball = new Circle2F(center, radius);
            _placement = Slice_placement.NORMAL;
            create_arc_circle(new Point2F(center.X + radius, center.Y), dir);
        }
    }
}
