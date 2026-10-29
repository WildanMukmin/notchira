using System;

namespace Notchira
{
    sealed class Spring
    {
        const float K = 320f, C = 26f;
        public float X, V, T;

        public Spring(float v) { X = T = v; }

        // Returns true while still moving.
        public bool Step(float dt)
        {
            int n = (int)Math.Ceiling(dt / 0.004f);
            float h = dt / n;
            for (int i = 0; i < n; i++)
            {
                V += ((T - X) * K - V * C) * h;
                X += V * h;
            }
            if (Math.Abs(T - X) < 0.05f && Math.Abs(V) < 0.3f) { X = T; V = 0; return false; }
            return true;
        }
    }
}
