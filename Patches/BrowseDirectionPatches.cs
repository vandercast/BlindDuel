using HarmonyLib;
using Il2CppYgomSystem;
using UnityEngine;

namespace BlindDuel
{
    [HarmonyPatch(typeof(GamePad_PC), nameof(GamePad_PC.GetKeyDown))]
    class PatchBrowseDirectionGetKeyDown
    {
        static void Postfix(int Type, ref bool __result)
        {
            BrowseDirectionTracker.TrackKeyDown(Type, __result);
        }
    }

    [HarmonyPatch(typeof(GamePad_PC), nameof(GamePad_PC.GetKey))]
    class PatchBrowseDirectionGetKey
    {
        static void Postfix(int Type, ref bool __result)
        {
            BrowseDirectionTracker.TrackKey(Type, __result);
        }
    }

    static class BrowseDirectionTracker
    {
        public static void TrackKeyDown(int type, bool result)
        {
            if (DuelState.LastBrowsePosition < 0) return;
            if (!Application.isFocused) return;

            if (type == GamePad.BUTTON_DOWN && (result || Input.GetKeyDown(KeyCode.DownArrow)))
            {
                DuelState.BrowseDirection = 1;
                return;
            }

            if (type == GamePad.BUTTON_UP && (result || Input.GetKeyDown(KeyCode.UpArrow)))
            {
                DuelState.BrowseDirection = -1;
            }
        }

        public static void TrackKey(int type, bool result)
        {
            if (DuelState.LastBrowsePosition < 0) return;
            if (!Application.isFocused) return;

            if (type == GamePad.BUTTON_DOWN && (result || Input.GetKey(KeyCode.DownArrow)))
            {
                DuelState.BrowseDirection = 1;
                return;
            }

            if (type == GamePad.BUTTON_UP && (result || Input.GetKey(KeyCode.UpArrow)))
            {
                DuelState.BrowseDirection = -1;
            }
        }
    }
}
