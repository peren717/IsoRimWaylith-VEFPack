using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace IsoRimVEFPack
{
    // The pack's IsoRim part (needs IsoRim Api 20; older: nothing, logged once). Works with or without IsoRimZ.
    // Beams (below) and VEF things drawn as IsoRim models (Models3D.cs).
    [StaticConstructorOnStartup]
    public static class PackMain
    {
        internal const string Tag = "[IsoRim VE Framework Pack] ";

        static PackMain()
        {
            try
            {
                if (IsoRim.Api.Version < 20)
                {
                    Log.Message(Tag + "IsoRim Waylith has no drawn-space API (Api 20): VEF beams keep VEF's flat look in the 3D view.");
                    return;
                }
                Harmony h = new Harmony("peren.isorimwaylith.vefpack");
                h.PatchAll(typeof(PackMain).Assembly);
                Models3D.Run(h);   // VEF things under IsoRim models: pipe storage bars, the extended biosculpter pod
            }
            catch (Exception e)
            {
                Log.Error(Tag + "Setup failed (VEF beams keep VEF's flat look): " + e);
            }
        }
    }

    // VEF beam projectiles (VEF.Weapons.ExpandableProjectile: VWE's flamethrower and other "expandable" beams) in IsoRim's 3D
    // view. VEF draws a beam as one flat card on the ground from the shooter to the beam's tip, so in 3D it lay on the floor.
    // Here the card runs through the air: from the muzzle IsoRim fires the shooter's bullets from (Api.DrawnStart: the
    // weapon's barrel end at its height, for a shooter on a hidden floor where its weapon would be) toward the height of what
    // the beam is aimed at (Api.AimHeight) at its destination, grown along that line as far as VEF grows the beam, turned
    // about its length to face the camera. Between IsoRimZ floors the line runs from the shooter's muzzle on its floor to the
    // target on the other (the points are placed through Api.DrawnPosition, which puts each on its floor), so the beam is
    // sloped instead of flat under the shooter's slab. Its width, length factor and animated material are VEF's. Only the
    // drawing changes (damage and fire cells are VEF's, with the IsoRimZ part's floor rule). With the 3D view off, VEF draws.
    public static class Beams3D
    {
        // Tests: beams drawn here, beams left to VEF (3D off or no usable line), the last drawn line (drawn space).
        public static int Drawn, LeftToVef;
        public static Vector3 LastStart, LastTip, LastEnd;
        public static float LastWidth, LastLength;
        public static Thing LastLauncher;

        private static Type type;
        private static Func<object, Vector3> curPosition, startingPosition;
        private static Func<object, Material> projectileMat;
        private static FieldInfo totalSizeScale, widthScaleFactor;
        private static readonly AccessTools.FieldRef<Projectile, Vector3> destinationRef = AccessTools.FieldRefAccess<Projectile, Vector3>("destination");

        internal static MethodBase Target()
        {
            type = AccessTools.TypeByName("VEF.Weapons.ExpandableProjectile");
            Type defType = AccessTools.TypeByName("VEF.Weapons.ExpandableProjectileDef");
            MethodInfo draw = type != null ? AccessTools.Method(type, "DrawProjectile", Type.EmptyTypes) : null;
            MethodInfo cur = type != null ? AccessTools.PropertyGetter(type, "CurPosition") : null;
            MethodInfo start = type != null ? AccessTools.PropertyGetter(type, "StartingPosition") : null;
            MethodInfo mat = type != null ? AccessTools.PropertyGetter(type, "ProjectileMat") : null;
            totalSizeScale = defType != null ? AccessTools.Field(defType, "totalSizeScale") : null;
            widthScaleFactor = defType != null ? AccessTools.Field(defType, "widthScaleFactor") : null;
            if (draw == null || cur == null || start == null || mat == null || totalSizeScale == null || widthScaleFactor == null)
            {
                Log.Warning(PackMain.Tag + "VEF's ExpandableProjectile drawing (DrawProjectile, CurPosition, StartingPosition, ProjectileMat, def scales) not found: beams keep VEF's flat look in the 3D view.");
                return null;
            }
            curPosition = o => (Vector3)cur.Invoke(o, null);
            startingPosition = o => (Vector3)start.Invoke(o, null);
            projectileMat = o => (Material)mat.Invoke(o, null);
            return draw;
        }

        // True: drawn here (VEF's flat card skipped).
        internal static bool Draw(Projectile p)
        {
            if (!IsoRim.Api.ViewReady || p == null || p.Map == null)
            {
                LeftToVef++;
                return false;
            }
            Vector3 start = startingPosition(p);    // VEF's beam start (with the IsoRimZ part: the shooter's column on the beam's floor)
            Vector3 cur = curPosition(p);           // the beam's tip on VEF's flat path
            Vector3 dest = destinationRef(p);
            float whole = Flat(start, dest);
            float done = Flat(start, cur);
            if (whole < 0.01f || done < 0.01f)
            {
                LeftToVef++;
                return false;
            }
            float squash = IsoRim.Api.HeightSquash;
            // The muzzle: x and z on the shot's rows, y the true height over the ground (the shooter's floor included); put
            // where those rows are drawn, then at that true height.
            Vector3 ds = IsoRim.Api.DrawnStart(p);
            Vector3 s = IsoRim.Api.DrawnPosition(new Vector3(ds.x, 0f, ds.z));
            s.y = ds.y / squash;
            // The end: the destination at the aimed-at height over its floor.
            Vector3 e = IsoRim.Api.DrawnPosition(new Vector3(dest.x, IsoRim.Api.AimHeight(p), dest.z));
            Vector3 tip = Vector3.Lerp(s, e, Mathf.Clamp01(done / whole));
            // The card is laid out in true space (y in cells: drawn y times the squash), where lengths, widths and the turn
            // toward the camera are true, then squashed into drawn space.
            Vector3 sT = new Vector3(s.x, s.y * squash, s.z);
            Vector3 tipT = new Vector3(tip.x, tip.y * squash, tip.z);
            Vector3 axis = tipT - sT;
            if (axis.sqrMagnitude < 1e-6f)
            {
                LeftToVef++;
                return false;
            }
            object def = p.def;
            float tss = (float)totalSizeScale.GetValue(def);
            float wsf = (float)widthScaleFactor.GetValue(def);
            float flatLen = done * tss;
            float width = flatLen * wsf * (flatLen / whole);   // as VEF: the beam widens as it reaches out
            float length = axis.magnitude * tss;
            Vector3 dir = axis.normalized;
            Vector3 toCamera = -(Quaternion.Euler(IsoRim.Api.ViewPitch, IsoRim.Api.ViewYaw, 0f) * Vector3.forward);
            Vector3 up = toCamera - dir * Vector3.Dot(toCamera, dir);
            if (up.sqrMagnitude < 1e-6f)
            {
                up = Vector3.up - dir * Vector3.Dot(Vector3.up, dir);
            }
            Quaternion rot = Quaternion.LookRotation(dir, up.normalized);
            Vector3 centre = sT + dir * (length * 0.5f);
            Matrix4x4 m = Matrix4x4.Scale(new Vector3(1f, 1f / squash, 1f)) * Matrix4x4.TRS(centre, rot, new Vector3(width, 1f, length));
            Graphics.DrawMesh(MeshPool.plane10, m, projectileMat(p), 0);
            Drawn++;
            LastStart = s;
            LastTip = tip;
            LastEnd = e;
            LastWidth = width;
            LastLength = length;
            LastLauncher = p.Launcher;
            return true;
        }

        private static float Flat(Vector3 a, Vector3 b) => new Vector2(b.x - a.x, b.z - a.z).magnitude;
    }

    // ExpandableProjectile.DrawProjectile: drawn here in the 3D view.
    [HarmonyPatch]
    public static class Beam_DrawProjectile_Patch
    {
        private static MethodBase target;
        private static bool tried;

        public static bool Prepare()
        {
            if (!tried)
            {
                tried = true;
                target = Beams3D.Target();
            }
            return target != null;
        }

        public static MethodBase TargetMethod() => target;

        public static bool Prefix(Projectile __instance)
        {
            try
            {
                return !Beams3D.Draw(__instance);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PackMain.Tag + "Drawing a VEF beam in 3D failed (VEF draws it): " + e, 0x7e9a31);
                return true;
            }
        }
    }
}
