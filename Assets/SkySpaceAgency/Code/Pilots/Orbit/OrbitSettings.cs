using KTools;

namespace K2D2.OrbitPlanning
{
    // Orbit tab targets. A disabled target keeps the current orbit's value.
    public static class OrbitSettings
    {
        public static Setting<bool> ap_enabled = new("orbit.ap_enabled", false);
        public static Setting<float> ap_km = new("orbit.ap_km", 100);
        public static Setting<bool> pe_enabled = new("orbit.pe_enabled", false);
        public static Setting<float> pe_km = new("orbit.pe_km", 100);
        public static Setting<bool> inc_enabled = new("orbit.inc_enabled", false);
        public static ClampSetting<float> inc_deg = new("orbit.inc_deg", 0, 0, 180);
    }
}
