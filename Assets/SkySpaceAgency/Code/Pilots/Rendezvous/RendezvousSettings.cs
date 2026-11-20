using KTools;

namespace SkySpaceAgency.Rendezvous
{
    // The Rendezvous tab's choices, kept between sessions.
    public static class RendezvousSettings
    {
        public static Setting<string> target_id = new("rendezvous.target_id", "");
        public static Setting<bool> prepare_only = new("rendezvous.prepare_only", false);
    }
}
