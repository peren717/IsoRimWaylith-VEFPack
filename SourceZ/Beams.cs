using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ProjectZ;
using UnityEngine;
using Verse;

namespace IsoRimVEFPackZ
{
    // VEF beam projectiles (VEF.Weapons.ExpandableProjectile: VWE's flamethrower and every other "expandable" beam) fired
    // between IsoRimZ floors (audit vef-floors-audit.md N1). ProjectZ flies a shot between floors in the rows of the target's
    // floor: its Launch postfix moves the projectile (origin, Position) into that floor's band, in the shooter's column. A beam
    // also draws and damages from its StartingPosition, which VEF reads from the launcher's real cell every time, so between
    // floors the beam spanned the flat map from the shooter's band to the target's: a huge flame sheet, fire and damage on
    // every cell of that line on both floors, and a radius loop of ~290 cells ("Not enough squares to get to radius") every
    // damage tick.
    //   - StartingPosition: the same column on the projectile's floor (the band ProjectZ moved it to). The field VEF keeps
    //     is set to it too (its impact fleck reads the field).
    //   - SetDestinationToMax (defs with reachMaxRangeAlways): the extended destination stays inside the projectile's band.
    //   - MakeProjectileLine (the cells a beam damages): cells off the projectile's floor (void rows, another floor's band)
    //     are dropped, so a beam near a band edge never reaches across.
    // On one floor nothing changes. VEF is reached by name: anything missing leaves beams as VEF draws them (logged once).
    public static class Beams
    {
        internal const string TypeName = "VEF.Weapons.ExpandableProjectile";

        private static Type type;
        private static FieldInfo startingField;
        private static readonly AccessTools.FieldRef<Projectile, Vector3> destinationField = AccessTools.FieldRefAccess<Projectile, Vector3>("destination");
        private static readonly AccessTools.FieldRef<Projectile, int> ticksToImpactField = AccessTools.FieldRefAccess<Projectile, int>("ticksToImpact");
        private static readonly MethodInfo startingTicks = AccessTools.PropertyGetter(typeof(Projectile), "StartingTicksToImpact");
        private static readonly HashSet<string> told = new HashSet<string>();

        // Tests: how many times a beam's start was moved to the projectile's floor, cells dropped, destinations clipped.
        public static int Moved, Dropped, Clipped;

        internal static Type Type
        {
            get
            {
                if (type == null)
                {
                    type = AccessTools.TypeByName(TypeName);
                    FieldInfo f = type != null ? AccessTools.Field(type, "startingPosition") : null;
                    if (f != null && f.FieldType == typeof(Vector3))
                    {
                        startingField = f;
                    }
                }
                return type;
            }
        }

        internal static MethodBase Find(string what, Func<Type, MethodBase> get)
        {
            MethodBase m = Type != null ? get(Type) : null;
            if (m == null && told.Add(what))
            {
                Log.Warning(PackZ.Tag + $"VEF's ExpandableProjectile.{what} not found: beams between floors are left as VEF draws them.");
            }
            return m;
        }

        // The floor of the projectile's own cell (ProjectZ moved it to the target's band), or -1.
        private static int FloorOf(Projectile p, out Map map)
        {
            map = p?.Map;
            if (map == null || !Api.HasFloors(map) || !p.Position.InBounds(map))
            {
                return -1;
            }
            return Api.FloorOf(p.Position, map);
        }

        // v moved to the same column on floor k (unchanged when it is already there or has no column).
        private static Vector3 OnFloor(Vector3 v, int k, Map map)
        {
            IntVec3 c = v.ToIntVec3();
            if (!c.InBounds(map))
            {
                return v;
            }
            int kv = Api.FloorOf(c, map);
            if (kv < 0 || kv == k)
            {
                return v;
            }
            IntVec3 t = Api.CellOnFloor(Api.GroundCellOf(c, map), k, map);
            if (!t.IsValid)
            {
                return v;
            }
            v.z += t.z - c.z;
            return v;
        }

        internal static void Start(Projectile p, ref Vector3 result)
        {
            int k = FloorOf(p, out Map map);
            if (k < 0)
            {
                return;
            }
            Vector3 moved = OnFloor(result, k, map);
            if (moved != result)
            {
                result = moved;
                Moved++;
                if (startingField != null)
                {
                    startingField.SetValue(p, moved);
                }
            }
        }

        internal static void ClipDestination(Projectile p)
        {
            int k = FloorOf(p, out Map map);
            if (k < 0)
            {
                return;
            }
            CellRect ground = Api.GroundRect(map);
            int lo = Api.CellOnFloor(new IntVec3(ground.minX, 0, ground.minZ), k, map).z;
            int hi = Api.CellOnFloor(new IntVec3(ground.minX, 0, ground.maxZ), k, map).z;
            ref Vector3 d = ref destinationField(p);
            Vector3 o = d;
            d.z = Mathf.Clamp(d.z, lo, hi + 0.99f);
            d.x = Mathf.Clamp(d.x, 0f, map.Size.x - 0.01f);
            if (d != o)
            {
                Clipped++;
                if (startingTicks != null)
                {
                    ticksToImpactField(p) = Mathf.Max(1, Mathf.CeilToInt((float)startingTicks.Invoke(p, null)));
                }
            }
        }

        internal static void KeepOnFloor(Projectile p, HashSet<IntVec3> cells)
        {
            if (cells == null || cells.Count == 0)
            {
                return;
            }
            int k = FloorOf(p, out Map map);
            if (k < 0)
            {
                return;
            }
            Dropped += cells.RemoveWhere(c => !c.InBounds(map) || Api.FloorOf(c, map) != k);
        }
    }

    // ---- patches (applied with the rest by PatchAll; each skips itself when VEF changed)

    [HarmonyPatch]
    public static class Beam_StartingPosition_Patch
    {
        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod() => Beams.Find("StartingPosition", t => AccessTools.PropertyGetter(t, "StartingPosition"));

        public static void Postfix(Projectile __instance, ref Vector3 __result)
        {
            try
            {
                Beams.Start(__instance, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PackZ.Tag + "Moving a beam's start to its floor failed: " + e, 0x7e9a21);
            }
        }
    }

    [HarmonyPatch]
    public static class Beam_SetDestinationToMax_Patch
    {
        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod() => Beams.Find("SetDestinationToMax", t => AccessTools.Method(t, "SetDestinationToMax"));

        public static void Postfix(Projectile __instance)
        {
            try
            {
                Beams.ClipDestination(__instance);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PackZ.Tag + "Keeping a beam's range on its floor failed: " + e, 0x7e9a22);
            }
        }
    }

    [HarmonyPatch]
    public static class Beam_MakeProjectileLine_Patch
    {
        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod() => Beams.Find("MakeProjectileLine", t => AccessTools.Method(t, "MakeProjectileLine"));

        public static void Postfix(Projectile __instance, HashSet<IntVec3> __result)
        {
            try
            {
                Beams.KeepOnFloor(__instance, __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PackZ.Tag + "Keeping a beam's cells on its floor failed: " + e, 0x7e9a23);
            }
        }
    }
}
