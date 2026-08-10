using System;
using GTA;
using GTA.Math;
using GTAZ.Core;

namespace GTAZ.UI
{
    // Soft-aim targeting (SoftAimKey, default H): toggles a screen-center reticule that locks
    // onto the ped under the crosshair without raising a weapon (raising one at a neutral
    // survivor is what flipped them hostile pre-recruit). Generic by design - this is the
    // targeting/highlight system, not a recruiting feature: the owner supplies a validity
    // predicate so anything (survivors today, peds/items/props tomorrow) can be locked, and the
    // consumer decides what the lock does (recruiting is currently the only action). Toggle (not
    // hold) so the player can move and look freely while soft-aiming. A locked ped is highlighted
    // with a green cylinder marker. Exposes `LockedTarget` for the consumer to act on.
    public class SoftAimController(Func<Ped, bool> isValidTarget)
    {
        private bool _softAimEnabled;
        private bool _keyWasDown;
        private int _timer;
        private GTA.UI.CustomSprite _reticleRing;
        private const string ReticleRingPath = "./scripts/GTAZ/ReticleCircle.png";

        public Ped LockedTarget { get; private set; }
        public bool IsEnabled => _softAimEnabled;

        public void Update()
        {
            var config = ModConfig.Current;

            // Toggle on the key press edge (rising edge only - a held key must not re-toggle).
            bool keyDown = Game.IsKeyPressed(config.SoftAimKey);
            if (keyDown && !_keyWasDown)
                _softAimEnabled = !_softAimEnabled;
            _keyWasDown = keyDown;

            if (!_softAimEnabled)
            {
                LockedTarget = null;
                _timer = 0;
                return;
            }

            if (++_timer % 3 == 0)
                LockedTarget = FindTargetUnderCrosshair();

            DrawAimReticule(LockedTarget != null);

            var target = LockedTarget;
            if (target?.Exists() is true && !target.IsDead)
            {
                Vector3 above = target.Position + new Vector3(0f, 0f, target.HeightAboveGround + 0.9f);
                World.DrawMarker(MarkerType.Cylinder, above, Vector3.Zero, Vector3.Zero,
                    new Vector3(0.35f, 0.35f, 0.35f), System.Drawing.Color.FromArgb(160, 0, 255, 0));
            }
        }

        public void ClearLock()
        {
            LockedTarget = null;
        }

        private Ped FindTargetUnderCrosshair()
        {
            var hit = World.Raycast(GameplayCamera.Position, GameplayCamera.Direction, 250f,
                IntersectFlags.Peds | IntersectFlags.Map | IntersectFlags.Vehicles);
            if (!hit.DidHit)
                return null;
            if (hit.HitEntity is not Ped target)
                return null;
            if (target.IsPlayer || target.IsDead || target.IsInGroup)
                return null;

            return isValidTarget(target) ? target : null;
        }

        // Draw the soft-aim reticule at the screen center (GTA drawing coords are 0-1 fractions,
        // 0.5/0.5 = center). It's a smooth anti-aliased circle ring drawn from a custom texture,
        // green when locked on a valid target, white otherwise.
        private void DrawAimReticule(bool locked)
        {
            int r = locked ? 0 : 255;
            const int g = 255;
            int b = locked ? 0 : 255;
            int alpha = locked ? 230 : 170;

            if (TryGetReticleRing(out var ring))
            {
                ring.Color = System.Drawing.Color.FromArgb(alpha, r, g, b);
                ring.Draw();
            }
        }

        // A proper circle ring drawn from a custom texture - no guessing at game texture names.
        // GTA.UI.CustomSprite loads an external PNG (SHVDN3 builds the texture from the file), so
        // the ring PNG is generated once at runtime with GDI+ anti-aliasing and drawn at the
        // screen center on the 1280x720 drawing base. Returns false if the PNG can't be written
        // or loaded, in which case no reticule is drawn that frame.
        private bool TryGetReticleRing(out GTA.UI.CustomSprite ring)
        {
            ring = _reticleRing;
            if (ring is not null)
                return true;

            try
            {
                if (!System.IO.File.Exists(ReticleRingPath))
                    WriteReticleRingPng();

                _reticleRing = new GTA.UI.CustomSprite(ReticleRingPath,
                    new System.Drawing.SizeF(34f, 34f),
                    new System.Drawing.PointF(640f, 360f),
                    System.Drawing.Color.White, 0f, centered: true);
                ring = _reticleRing;
                return true;
            }
            catch
            {
                _reticleRing = null;
                return false;
            }
        }

        // Renders a thin anti-aliased ring (ellipse stroke on a transparent 128x128 canvas) and
        // writes it to the scripts/GTAZ folder so the CustomSprite can load it on next use.
        private static void WriteReticleRingPng()
        {
            using var bmp = new System.Drawing.Bitmap(128, 128, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 14f);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            g.DrawEllipse(pen, 14f, 14f, 100f, 100f);
            System.IO.Directory.CreateDirectory("./scripts/GTAZ");
            bmp.Save(ReticleRingPath, System.Drawing.Imaging.ImageFormat.Png);
        }
    }
}
