using System;

namespace GTAZ.Utils
{
    public static class MathUtils
    {
        public static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
