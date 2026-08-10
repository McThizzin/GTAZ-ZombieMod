using GTA.Native;
using GTAZ.Core;
using GTAZ.UI;

namespace GTAZ.Systems
{
    // Phase 5 gameplay restrictions (DOCS/06-Roadmap.md §5). Per-frame re-asserts so the engine can't
    // silently clear them mid-session (same lesson as the trainer god modes, lesson 35):
    //  - character switching disabled (Alt character wheel + the F5/F6/F8 direct selects)
    //  - phone disabled (phone-open input disabled each frame, except while a menu is open)
    // The soft-aim (H key) reticule lives in UI/SoftAimController, not here.
    public static class GameplaySettings
    {
        // PAD control indexes (GTA.Control values): 19 = CharacterWheel (Alt),
        // 166-169 = the direct select-character keys (Michael/Franklin/Trevor/Multiplayer).
        private const int ControlCharacterWheel = 19;
        private const int ControlSelectMichael = 166;
        private const int ControlSelectFranklin = 167;
        private const int ControlSelectTrevor = 168;
        private const int ControlSelectMultiplayer = 169;

        // INPUT_PHONE (27) - Arrow Up / scrollwheel press opens the phone. Shared with the
        // frontend menu Up-navigation, so it's only disabled while no in-game menu is open.
        private const int ControlPhone = 27;

        public static void Tick()
        {
            var config = ModConfig.Current;

            if (config.DisableCharacterSwitching)
            {
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 2, ControlCharacterWheel, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 2, ControlSelectMichael, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 2, ControlSelectFranklin, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 2, ControlSelectTrevor, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 2, ControlSelectMultiplayer, true);
            }

            // The phone-open input (INPUT_PHONE, 27) is the reliable way to block the phone:
            // the dedicated _DISABLE_PHONE_THIS_FRAME native isn't present in this game
            // version's native table (SHVDN3's Function.Call only takes the 32-bit Hash enum,
            // and the game can't resolve it - fatal "can't find native"). Control 27 is also
            // the frontend Up-navigation, so the disable only applies while no menu is open -
            // the LemonUI/trainer menus stay fully navigable, and gameplay Arrow Up can't open
            // the phone.
            if (config.DisablePhone && !MenuManager.Instance.IsAnyMenuOpen)
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 2, ControlPhone, true);
        }
    }
}
