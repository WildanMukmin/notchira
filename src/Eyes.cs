using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Notchira
{
    // The mascot: two eyes drawn with GDI+ paths, so there are no image assets to ship.
    // Smooth values (look, happy, wide, droop) are springs; blink and nod are short scripted curves.
    sealed class Eyes
    {
        public readonly Spring LookX = new Spring(0), LookY = new Spring(0);
        public readonly Spring Happy = new Spring(0), Wide = new Spring(0), Droop = new Spring(0);

        readonly Stopwatch clk = Stopwatch.StartNew();
        double blink1 = -10, blink2 = -10, nod = -10;
        const double BlinkLen = 0.16, NodLen = 0.38;

        double Now() { return clk.Elapsed.TotalSeconds; }

        public void Blink(bool twice)
        {
            blink1 = Now();
            blink2 = twice ? blink1 + 0.24 : -10;
        }

        public void Nod() { nod = Now(); }

        static float Curve(double t, double len)
        {
            if (t < 0 || t > len) return 0;
            return (float)Math.Sin(Math.PI * t / len);
        }

        float BlinkAmount()
        {
            double n = Now();
            return Math.Max(Curve(n - blink1, BlinkLen), Curve(n - blink2, BlinkLen));
        }

        // True while anything is still moving, so the caller knows whether to keep its frame timer running.
        public bool Step(float dt)
        {
            bool moving = false;
            moving |= LookX.Step(dt);
            moving |= LookY.Step(dt);
            moving |= Happy.Step(dt);
            moving |= Wide.Step(dt);
            moving |= Droop.Step(dt);
            double n = Now();
            if (n - blink1 < BlinkLen || n - blink2 < BlinkLen || n - nod < NodLen) moving = true;
            return moving;
        }

        static float Clamp(float v, float lo, float hi) { return v < lo ? lo : v > hi ? hi : v; }

        // Lower part of an ellipse below a lid line: cover 0 is a full ellipse, 1 is closed.
        static GraphicsPath Chord(RectangleF r, float cover)
        {
            cover = Clamp(cover, 0f, 0.97f);
            float th = (float)(Math.Asin(2 * cover - 1) * 180 / Math.PI);
            var p = new GraphicsPath();
            p.AddArc(r, th, 180 - 2 * th);
            p.CloseFigure();
            return p;
        }

        // cx, cy is the point between the eyes; s scales the whole pair.
        public void Draw(Graphics g, float cx, float cy, float s, float alpha)
        {
            if (alpha < 0.02f) return;
            float cover = Math.Max(BlinkAmount(), Math.Min(0.9f, 0.55f * Droop.X));
            float happy = Clamp(Happy.X, 0f, 1f);
            float wide = Clamp(Wide.X, 0f, 1f);
            float droop = Clamp(Droop.X, 0f, 1f);
            float dy = 1.7f * s * Curve(Now() - nod, NodLen);
            DrawEye(g, cx - 11 * s, cy + dy, s, alpha, cover, happy, wide);
            DrawEye(g, cx + 11 * s, cy + dy, s, alpha, cover, happy, wide);
            DrawMouth(g, cx, cy + dy, s, alpha, happy, wide, droop);
        }

        // A small mouth below the eyes: a bezier smile/frown, or a round "o" while listening or surprised.
        void DrawMouth(Graphics g, float cx, float cy, float s, float alpha, float happy, float wide, float droop)
        {
            float my = cy + 7.3f * s;
            if (wide > 0.3f && happy < 0.3f)
            {
                float r = (1.1f + 0.9f * wide) * s;
                using (var b = new SolidBrush(Color.FromArgb((int)(190 * alpha * wide), 255, 255, 255)))
                    g.FillEllipse(b, cx - r / 2, my - r / 2, r, r);
                return;
            }
            float half = 3.6f * s;
            float curve = happy * 2.3f * s - droop * 1.1f * s;
            using (var path = new GraphicsPath())
            {
                path.AddBezier(
                    new PointF(cx - half, my), new PointF(cx - half * 0.5f, my + curve),
                    new PointF(cx + half * 0.5f, my + curve), new PointF(cx + half, my));
                using (var pen = new Pen(Color.FromArgb((int)(210 * alpha), 255, 255, 255), Math.Max(1.1f, 1.4f * s)))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    g.DrawPath(pen, path);
                }
            }
        }

        void DrawEye(Graphics g, float x, float y, float s, float alpha, float cover, float happy, float wide)
        {
            float k = 1 + 0.2f * wide;
            float w = 9 * s * k, h = 12 * s * k;
            var rect = new RectangleF(x - w / 2, y - h / 2, w, h);

            if (happy < 0.98f)
            {
                float a = alpha * (1 - happy);
                using (var path = Chord(rect, cover))
                {
                    using (var b = new SolidBrush(Color.FromArgb((int)(240 * a), 255, 255, 255))) g.FillPath(b, path);
                    if (cover < 0.8f)
                    {
                        var saved = g.Clip;
                        g.SetClip(path, CombineMode.Intersect);
                        float pr = (wide > 0.5f ? 0.23f : 0.31f) * w;
                        float px = x + LookX.X * 0.2f * w, py = y + LookY.X * 0.17f * h;
                        using (var b = new SolidBrush(Color.FromArgb((int)(255 * a), 18, 18, 26)))
                            g.FillEllipse(b, px - pr, py - pr, pr * 2, pr * 2);
                        float hr = 0.1f * w;
                        using (var b = new SolidBrush(Color.FromArgb((int)(230 * a), 255, 255, 255)))
                            g.FillEllipse(b, px - pr * 0.55f - hr, py - pr * 0.6f - hr, hr * 2, hr * 2);
                        g.Clip = saved;
                    }
                }
            }
            if (happy > 0.02f)
            {
                using (var pen = new Pen(Color.FromArgb((int)(245 * alpha * happy), 255, 255, 255), Math.Max(1.6f, 2.3f * s)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawArc(pen, x - w * 0.5f, y - h * 0.18f, w, h * 0.85f, 205, 130);
                }
            }
        }
    }
}
