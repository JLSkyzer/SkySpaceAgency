using KSP.Game;

namespace SkySpaceAgency
{
    /// <summary>What the plugin does this frame after reading the scene (SceneTransition.Next).</summary>
    public struct SceneStep
    {
        /// <summary>Pilots, staging and the rails warp watchdog run this frame.</summary>
        public bool InFlight;
        /// <summary>The main window may be shown (flight and map view, not photo mode).</summary>
        public bool WindowAllowed;
        /// <summary>Reset the controllers once, with this cause (null: no reset).</summary>
        public ResetCause? Reset;
        public bool CloseWindow;
        public bool OpenWindow;
        /// <summary>The window was closed by a scene change and opens again on the way back.</summary>
        public bool HiddenByScene;
    }

    /// <summary>
    /// The plugin's scene logic, from K2-D2 1.4.1 (IanMealworm): worked out once per frame, the
    /// controllers are reset once when flight is left (not every frame after), photo mode counts as
    /// flight (a pilot keeps flying while the player takes a picture), and the window is hidden
    /// outside flight and in photo mode, then opened again on the way back if it was open.
    /// </summary>
    public static class SceneTransition
    {
        public static bool IsFlight(GameState state) =>
            state == GameState.FlightView || state == GameState.Map3DView || state == GameState.PhotoMode;

        public static bool IsWindowScene(GameState state) =>
            state == GameState.FlightView || state == GameState.Map3DView;

        /// <param name="wasInFlight">InFlight of the previous frame.</param>
        /// <param name="enabled">The mod is on in this campaign (KerbalMod.Enabled).</param>
        /// <param name="state">The game state, null while the game is not set up.</param>
        /// <param name="windowOpen">The main window exists and is open.</param>
        /// <param name="hiddenByScene">HiddenByScene of the previous frame.</param>
        public static SceneStep Next(bool wasInFlight, bool enabled, GameState? state, bool windowOpen, bool hiddenByScene)
        {
            var step = new SceneStep { HiddenByScene = hiddenByScene };
            if (!enabled)
            {
                // Nothing runs in a campaign that has the mod off; the window does not come back
                // by itself in another campaign.
                if (wasInFlight)
                    step.Reset = ResetCause.ModDisabled;
                step.CloseWindow = windowOpen;
                step.HiddenByScene = false;
                return step;
            }

            if (state.HasValue)
            {
                step.InFlight = IsFlight(state.Value);
                step.WindowAllowed = IsWindowScene(state.Value);
            }
            if (wasInFlight && !step.InFlight)
                step.Reset = ResetCause.SceneChange;

            if (!step.WindowAllowed && windowOpen)
            {
                step.CloseWindow = true;
                step.HiddenByScene = true;
            }
            else if (step.WindowAllowed && hiddenByScene)
            {
                step.OpenWindow = !windowOpen;
                step.HiddenByScene = false;
            }
            return step;
        }
    }
}
