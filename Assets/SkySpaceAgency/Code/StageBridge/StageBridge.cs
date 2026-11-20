using System;
using KSP.Messages;
using KTools;
using UnityEngine;

namespace SkySpaceAgency.Bridge
{
    // The bridge to Redux's Stage Recovery (glue). Plan 2026-11-20-stage-recovery-bridge. Nothing changes for
    // the player: it reads Redux's state, logs what changes and what survives each universe reset, and offers
    // three guarded calls (stage_bridge_enabled, off by default) that nothing in the mod makes yet.
    //
    // Lines read `[SkySpaceAgency] [StageBridge] ...` in Player.log. What to look for in game: the checklist,
    // section 31 of docs/testing/2026-10-24-transfers-in-game-checklist.md.
    public static class StageBridge
    {
        public const string Prefix = "[StageBridge] ";
        public const float PollSeconds = 0.2f;

        /// <summary>Where the lines go: the plugin's logger (replaced by the tests).</summary>
        public static Action<string> Sink = line => SkySpaceAgencyPlugin.logger?.LogInfo(line);

        // Seams for the tests: Redux's state and the mod's identity.
        internal static Func<BridgeState> Reader = StageRecoveryAccess.Read;
        internal static Func<UniverseIdentity> Capturer = UniverseIdentityReader.Capture;

        static BridgeWatch _watch = new BridgeWatch();
        static UniverseIdentity _before;
        static string _beforeWhere;
        static int _resetCount;
        static object _subscribedTo;
        static float _nextPoll;
        static BridgePhase _lastPhase = BridgePhase.Idle;
        static bool _havePhase;
        static bool _warnedNoCenter;
        static string _lastProblem, _lastPollError;

        static void Log(string text) => Sink?.Invoke(Prefix + text);

        /// <summary>Forgets every memory of the session (the tests; the game never calls it).</summary>
        public static void ResetState()
        {
            _before = null;
            _beforeWhere = null;
            _resetCount = 0;
            _subscribedTo = null;
            _nextPoll = 0;
            _lastPhase = BridgePhase.Idle;
            _havePhase = false;
            _warnedNoCenter = false;
            _lastProblem = _lastPollError = null;
            _watch = new BridgeWatch();
        }

        // ---- start-up and the poll ----

        /// <summary>From the plugin's OnInitialized.</summary>
        public static void Init()
        {
            try
            {
                Subscribe();
            }
            catch (Exception e)
            {
                Log($"armed: failed ({e.GetType().Name}: {e.Message})");
            }
        }

        // Persistent subscriptions (the game keeps them across a MessageCenter shutdown). Done again if the
        // game's message center is ever another object than the one subscribed to.
        static void Subscribe()
        {
            var messages = GeneralTools.Game?.Messages;
            if (messages == null)
            {
                if (!_warnedNoCenter)
                {
                    _warnedNoCenter = true;
                    Log("armed: no message center yet, will retry");
                }
                return;
            }
            if (ReferenceEquals(messages, _subscribedTo)) return;
            messages.PersistentSubscribe<GameLoadFinishedMessage>(OnGameLoadFinished);
            messages.PersistentSubscribe<QuitToMainMenuStartedMessage>(OnQuitToMainMenu);
            _subscribedTo = messages;   // only once both subscriptions went through
            Log($"armed: persistent subscriptions on GameLoadFinishedMessage and QuitToMainMenuStartedMessage (message center #{UniverseIdentityReader.Id(messages)}); "
                + $"stage_bridge_enabled {StageBridgeSettings.Enabled.V}; Redux names {(StageRecoveryAccess.Resolved ? "resolved" : "NOT resolved")}"
                + (string.IsNullOrEmpty(StageRecoveryAccess.MissingNames) ? "" : $", missing: {StageRecoveryAccess.MissingNames}"));
        }

        /// <summary>From the plugin's Update: reads Redux's state every 0.2 s of real time and logs what changed.</summary>
        public static void Tick() => Tick(Time.realtimeSinceStartup);

        public static void Tick(float now)
        {
            if (now < _nextPoll) return;
            _nextPoll = now + PollSeconds;
            try
            {
                Subscribe();
                var state = Reader() ?? BridgeState.Unavailable;
                var lines = _watch.Observe(state);
                bool important = false;
                foreach (var line in lines)
                {
                    Log("event: " + line);
                    if (line.StartsWith("phase ") || line.StartsWith("recovery point")) important = true;
                }
                // Why Redux reads as unavailable or incomplete, once per distinct cause.
                string problem = StageRecoveryAccess.LastProblem;
                if (problem != null && problem != _lastProblem)
                {
                    _lastProblem = problem;
                    Log("reading Redux's stage recovery: " + problem);
                }
                if (important) Log("identity: " + UniverseComparison.Format(Capturer()));

                // The way back to Idle after a session. An unavailable or unknown read in between is not a phase.
                if (state.Available && state.Phase != BridgePhase.Unknown)
                {
                    bool backToIdle = _havePhase && state.Phase == BridgePhase.Idle && _lastPhase != BridgePhase.Idle;
                    _lastPhase = state.Phase;
                    _havePhase = true;
                    if (backToIdle)
                    {
                        After("phase back to Idle");
                        _before = null;   // that reset cycle is over: the next comparison needs its own before
                        _beforeWhere = null;
                    }
                }
                _lastPollError = null;
            }
            catch (Exception e)
            {
                string text = $"poll failed ({e.GetType().Name}: {e.Message})";
                if (text != _lastPollError)
                {
                    _lastPollError = text;
                    Log(text);
                }
            }
        }

        // ---- the universe resets ----

        /// <summary>From the plugin's OnCampaignUnloading, which Redux's ResetUniverse runs first.</summary>
        public static void OnCampaignUnloading() => TakeBefore("campaign unloading");

        /// <summary>From the plugin's OnCampaignLoaded.</summary>
        public static void OnCampaignLoaded() => After("campaign loaded");

        internal static void OnGameLoadFinished(MessageCenterMessage message)
        {
            // The game recycles the message right after this callback: copy what is needed now.
            bool success = (message as GameLoadFinishedMessage)?.IsSuccess ?? false;
            Log($"event: game load finished, success {success}");
            After("game load finished");
        }

        internal static void OnQuitToMainMenu(MessageCenterMessage message)
        {
            Log("event: quit to main menu started");
            TakeBefore("quit to main menu");
        }

        static void TakeBefore(string where)
        {
            try
            {
                _before = Capturer();
                _beforeWhere = where;
                _resetCount++;
                Log($"before reset #{_resetCount} ({where}): {UniverseComparison.Format(_before)}");
            }
            catch (Exception e)
            {
                Log($"before reset ({where}) failed ({e.GetType().Name}: {e.Message})");
            }
        }

        static void After(string where)
        {
            try
            {
                var after = Capturer();
                Log($"after ({where}): {UniverseComparison.Format(after)}");
                string versus = _before == null ? "" : $" vs before #{_resetCount} ({_beforeWhere})";
                foreach (var line in UniverseComparison.Compare(_before, after))
                    Log($"survival ({where}{versus}): {line}");
            }
            catch (Exception e)
            {
                Log($"after ({where}) failed ({e.GetType().Name}: {e.Message})");
            }
        }

        // ---- the diagnostic ----

        /// <summary>The diagnostic button: Redux's state, its recovery points and the mod's identity. Only reads.</summary>
        public static void LogState(string reason)
        {
            try
            {
                var state = Reader() ?? BridgeState.Unavailable;
                Log($"state ({reason}): {BridgeFormat.State(state)}; stage_bridge_enabled {StageBridgeSettings.Enabled.V}");
                if (!string.IsNullOrEmpty(StageRecoveryAccess.LastProblem))
                    Log($"last problem reading Redux: {StageRecoveryAccess.LastProblem}");
                foreach (var line in BridgeFormat.Records(state))
                    Log(line);
                Log($"identity ({reason}): {UniverseComparison.Format(Capturer())}");
            }
            catch (Exception e)
            {
                Log($"state ({reason}) failed ({e.GetType().Name}: {e.Message})");
            }
        }

        // ---- the guarded calls (nothing in the mod calls them yet) ----

        public static bool StartReplay(string recordId)
            => Report("StartReplay", BridgeCalls.TryStart(Reader() ?? BridgeState.Unavailable, recordId, StageBridgeSettings.Enabled.V, StageRecoveryAccess.StartReplay));

        public static bool CompleteActiveLanding()
            => Report("CompleteActiveLanding", BridgeCalls.TryComplete(Reader() ?? BridgeState.Unavailable, StageBridgeSettings.Enabled.V, StageRecoveryAccess.CompleteActiveLanding));

        public static bool ReturnToMission()
            => Report("ReturnToMission", BridgeCalls.TryReturn(Reader() ?? BridgeState.Unavailable, StageBridgeSettings.Enabled.V, StageRecoveryAccess.ReturnToMission));

        static bool Report(string name, BridgeCallResult result)
        {
            if (!result.Invoked)
            {
                Log($"call {name} refused ({result.Verdict}): {BridgeRules.Explain(result.Verdict)}");
                return false;
            }
            string why = result.Result || string.IsNullOrEmpty(StageRecoveryAccess.LastProblem) ? "" : $" ({StageRecoveryAccess.LastProblem})";
            Log($"called {name} -> {result.Result}{why}");
            return result.Result;
        }
    }
}
