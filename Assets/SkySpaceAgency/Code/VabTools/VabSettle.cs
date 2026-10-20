using System;

namespace K2D2.VabTools
{
    // How long the import waits for the VAB to show a loaded rocket. A fixed short wait declared
    // large rockets failed while their parts were still being placed, and the restore then ran on
    // top of them; so the wait grows with the part count, between a floor and a cap.
    public static class VabSettle
    {
        public const float MinTimeoutS = 10f;
        public const float PerPartS = 0.1f;
        public const float MaxTimeoutS = 60f;

        public static float TimeoutS(int expectedParts) =>
            Math.Min(MaxTimeoutS, Math.Max(MinTimeoutS, PerPartS * expectedParts));
    }
}
