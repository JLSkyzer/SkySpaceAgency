using KTools;

namespace SkySpaceAgency.Transfer
{
    // The Transfer tab's choices, kept between sessions.
    public static class TransferSettings
    {
        public static Setting<string> target_body = new("transfer.target_body", "");
        public static EnumSetting<ArrivalKind> arrival = new("transfer.arrival", ArrivalKind.Orbit);
        public static Setting<bool> low_circular = new("transfer.low_circular", true);
        public static Setting<float> pe_km = new("transfer.pe_km", 100);
        public static Setting<float> ap_km = new("transfer.ap_km", 100);
        public static Setting<bool> inc_enabled = new("transfer.inc_enabled", false);
        public static ClampSetting<float> inc_deg = new("transfer.inc_deg", 0, 0, 180);
        public static Setting<bool> prepare_only = new("transfer.prepare_only", false);
        public static Setting<string> max_trip_days = new("transfer.max_trip_days", "");
        public static Setting<bool> gravity_assist = new("transfer.gravity_assist", false);
    }
}
