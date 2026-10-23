using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ECM2;
using HarmonyLib;
using MultiplayerTemplate.Bootstrap;
using MultiplayerTemplate.Core.Completion;
using MultiplayerTemplate.Core.Input;
using MultiplayerTemplate.Player;
using Unity.Netcode;
using UnityEngine;

namespace Ladder
{
    /// <summary>
    /// An extension ladder: set it up against a wall, climb it, set bricks from it and step off at the top. One ladder per
    /// player, seen only by the player who set it up, kept per world in the mod's own file (the game's save is not
    /// touched). A world in which a ladder is set up counts as practice (no leaderboard score).
    /// </summary>
    [BepInPlugin(Guid, "Ladder", Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "michel.brickbybrick.ladder";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<KeyboardShortcut> PlaceKey;
        internal static ConfigEntry<KeyboardShortcut> LongerKey;
        internal static ConfigEntry<KeyboardShortcut> ShorterKey;
        internal static ConfigEntry<float> MaxLength;
        internal static ConfigEntry<float> ClimbSpeed;
        internal static LadderStore Store;

        /// <summary>World slot of the save the host has open (set by the game before it loads the world).</summary>
        internal static string CurrentSlot;
        internal static CompletionRun CurrentRun;

        private static readonly AccessTools.FieldRef<NetworkPlayerController, Character> CharacterOf =
            AccessTools.FieldRefAccess<NetworkPlayerController, Character>("character");
        private static readonly AccessTools.FieldRef<NetworkPlayerController, IInputReader> InputOf =
            AccessTools.FieldRefAccess<NetworkPlayerController, IInputReader>("input");

        private static string toast;
        private static float toastUntil;

        private NetworkPlayerController player;
        private LadderClimber climber;
        private LadderInstance ladder;
        private bool restored;
        private float nextSearch;

        private void Awake()
        {
            Log = Logger;
            PlaceKey = Config.Bind("Keys", "SetUp", new KeyboardShortcut(KeyCode.L),
                "Looking at a wall: set up the ladder there (it moves if it stands elsewhere). Looking at the ladder: take it down.");
            LongerKey = Config.Bind("Keys", "Longer", new KeyboardShortcut(KeyCode.PageUp), "Extend the ladder by 0.5 m.");
            ShorterKey = Config.Bind("Keys", "Shorter", new KeyboardShortcut(KeyCode.PageDown), "Retract the ladder by 0.5 m.");
            MaxLength = Config.Bind("Ladder", "MaxLength", 12f,
                new ConfigDescription("Longest the ladder extends (meters).", new AcceptableValueRange<float>(3f, 30f)));
            ClimbSpeed = Config.Bind("Ladder", "ClimbSpeed", 2.5f,
                new ConfigDescription("Climbing speed (meters per second).", new AcceptableValueRange<float>(0.5f, 8f)));
            Store = new LadderStore(System.IO.Path.Combine(Paths.ConfigPath, Guid + ".worlds.json"));
            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"Ladder {Version} loaded: {PlaceKey.Value} sets up and takes down, {LongerKey.Value}/{ShorterKey.Value} change the length.");
        }

        internal static void Toast(string text, float seconds = 6f)
        {
            toast = text;
            toastUntil = Time.unscaledTime + seconds;
        }

        private static bool IsHost => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

        private void Update()
        {
            if (player == null)
            {
                climber = null;
                restored = false;
                ladder?.Destroy();
                ladder = null;
                if (Time.unscaledTime < nextSearch)
                {
                    return;
                }
                nextSearch = Time.unscaledTime + 1f;
                foreach (NetworkPlayerController p in FindObjectsByType<NetworkPlayerController>(FindObjectsSortMode.None))
                {
                    if (p.IsSpawned && p.IsOwner && CharacterOf(p) != null)
                    {
                        player = p;
                        break;
                    }
                }
                if (player == null)
                {
                    return;
                }
                climber = CharacterOf(player).gameObject.AddComponent<LadderClimber>();
                climber.Init(CharacterOf(player), () => player != null ? InputOf(player) : null);
            }
            if (!restored)
            {
                restored = true;
                LadderSpec saved = IsHost && CurrentSlot != null ? Store.Get(CurrentSlot) : null;
                if (saved != null)
                {
                    Show(saved);
                    Log.LogInfo($"Ladder of world '{CurrentSlot}' set up again ({saved.length:0.0} m).");
                }
            }
            IInputReader input = InputOf(player);
            if (input == null || !input.GameplayInputEnabled)
            {
                return;
            }
            if (PlaceKey.Value.IsDown())
            {
                OnPlaceKey();
            }
            else if (ladder != null && LongerKey.Value.IsDown())
            {
                Resize(0.5f);
            }
            else if (ladder != null && ShorterKey.Value.IsDown())
            {
                Resize(-0.5f);
            }
        }

        private void OnPlaceKey()
        {
            Camera cam = player.OwnerCamera;
            if (cam == null)
            {
                return;
            }
            if (climber.IsClimbing)
            {
                Toast("Get off the ladder first.", 3f);
                return;
            }
            var ray = new Ray(cam.transform.position, cam.transform.forward);
            if (ladder != null && ladder.IsAimedAt(ray, 5f))
            {
                ladder.Destroy();
                ladder = null;
                climber.Ladder = null;
                Remember(null);
                Toast("Ladder taken down.", 3f);
                Log.LogInfo("Ladder taken down.");
                return;
            }
            if (!LadderGeometry.TryFromAim(ray, CharacterOf(player), MaxLength.Value, out LadderSpec spec, out string why))
            {
                Toast(why, 4f);
                return;
            }
            Show(spec);
            Remember(spec);
            Toast($"Ladder set up: {spec.length:0.0} m" + (spec.hasExit ? ", you can step off at the top." : ", no room to step off at the top.") +
                $" Walk into it to climb, {PlaceKey.Value} on it takes it down, {LongerKey.Value}/{ShorterKey.Value} change its length.", 7f);
        }

        private void Resize(float delta)
        {
            if (climber.IsClimbing)
            {
                return;
            }
            float length = Mathf.Clamp(ladder.Spec.length + delta, LadderGeometry.MinLength, MaxLength.Value);
            if (Mathf.Abs(length - ladder.Spec.length) < 0.01f)
            {
                Toast($"The ladder is {ladder.Spec.length:0.0} m (from {LadderGeometry.MinLength:0} to {MaxLength.Value:0} m).", 3f);
                return;
            }
            if (!LadderGeometry.TryResize(ladder.Spec, length, CharacterOf(player), out LadderSpec spec, out string why))
            {
                Toast(why, 3f);
                return;
            }
            Show(spec);
            Remember(spec);
            Toast($"Ladder {spec.length:0.0} m" + (spec.hasExit ? ", you can step off at the top." : "."), 3f);
        }

        private void Show(LadderSpec spec)
        {
            ladder?.Destroy();
            ladder = new LadderInstance(spec);
            if (climber != null)
            {
                climber.Ladder = ladder;
            }
        }

        /// <summary>Keeps the ladder with the world (host only: a client does not know the world's slot) and marks the
        /// world as practice the first time a ladder stands in it.</summary>
        private static void Remember(LadderSpec spec)
        {
            if (IsHost && CurrentSlot != null)
            {
                Store.Set(CurrentSlot, spec);
            }
            if (spec != null && IsHost && CurrentRun != null && CurrentRun.IsOpen && CurrentRun.IsEligible)
            {
                CurrentRun.Invalidate("Ladder mod: a ladder was set up.");
                Log.LogInfo("World marked as practice (no leaderboard score): a ladder was set up.");
            }
        }

        private void OnGUI()
        {
            if (toast == null || Time.unscaledTime > toastUntil)
            {
                return;
            }
            var style = new GUIStyle(GUI.skin.box) { fontSize = 20, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            GUI.Box(new Rect(Screen.width / 2f - 420f, 140f, 840f, 64f), toast, style);
        }
    }

    /// <summary>The slot of the world being opened: the game sets it before the world loads.</summary>
    [HarmonyPatch(typeof(AppBootstrap), nameof(AppBootstrap.ConfigureHostSaveSlot))]
    internal static class SaveSlotPatch
    {
        private static readonly AccessTools.FieldRef<AppBootstrap, string> SlotOf = AccessTools.FieldRefAccess<AppBootstrap, string>("hostSaveSlotId");

        private static void Postfix(AppBootstrap __instance)
        {
            Plugin.CurrentSlot = SlotOf(__instance);
        }
    }

    /// <summary>The world's leaderboard run: a new world drops the slot's old ladder; a world with a ladder counts as practice.</summary>
    [HarmonyPatch(typeof(CompletionRun), nameof(CompletionRun.Open))]
    internal static class CompletionRunPatch
    {
        private static void Postfix(CompletionRun __instance, bool isNewSave)
        {
            Plugin.CurrentRun = __instance;
            if (Plugin.CurrentSlot == null)
            {
                return;
            }
            if (isNewSave)
            {
                if (Plugin.Store.Get(Plugin.CurrentSlot) != null)
                {
                    Plugin.Store.Set(Plugin.CurrentSlot, null);
                    Plugin.Log.LogInfo($"New world in '{Plugin.CurrentSlot}': the old ladder of this slot is gone.");
                }
            }
            else if (Plugin.Store.Get(Plugin.CurrentSlot) != null && __instance.IsEligible)
            {
                __instance.Invalidate("Ladder mod: a ladder was set up.");
                Plugin.Log.LogInfo("World marked as practice (no leaderboard score): a ladder stands in it.");
            }
        }
    }
}
