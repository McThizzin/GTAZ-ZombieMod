using GTA;

namespace GTAZ.Utils
{
    // Shared "create-or-reuse when show, delete when hidden" minimap-blip pattern used by every
    // mod-controlled ped (zombies, survivors, faction members). Each caller owns a `Blip` field,
    // picks its sprite/color/scale, and calls Ensure(ref blip, ...) every tick so the config
    // toggles apply immediately.
    public static class BlipHelper
    {
        public static void Ensure(ref Blip blip, Ped ped, BlipSprite sprite, BlipColor color,
            float scale, bool show, string name = null)
        {
            if (show)
            {
                if (blip?.Exists() is not true)
                {
                    blip = ped.AddBlip();
                    blip.Sprite = sprite;
                    blip.Color = color;
                    blip.Scale = scale;
                    blip.IsShortRange = true;
                    if (name != null)
                        blip.Name = name;
                }
            }
            else
            {
                Delete(ref blip);
            }
        }

        public static void Delete(ref Blip blip)
        {
            blip?.Delete();
            blip = null;
        }
    }
}
