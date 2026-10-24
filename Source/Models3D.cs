using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace IsoRimVEFPack
{
    // VE Framework things that IsoRim draws as 3D models (any mod's, through any IsoRim model entry), IsoRim Api 20:
    //  - PipeSystem storage (PipeSystem.CompResourceStorage, every VE pipe-network tank): VEF draws its fill bar flat on the
    //    ground at the tank's 2D offset, so under a model it lay inside the tank. Under a model it stands up as a card over the
    //    model's middle (Api.BeginUpright, hinged on the bar, at the model's height + 0.3), as IsoRim's own work bars do; the
    //    bar is drawn at the tank's middle for that call (its 2D offset only fits the flat sprite).
    //  - VEF's extended biosculpter pod (VEF.Buildings.CompExtendedBiosculpterPod, any def using it): its PostDraw overrides
    //    the vanilla one IsoRim hooks and draws a flat background quad under the pod (CompProperties_ExtendedBiosculpterPod
    //    .drawBackground). Under a model that PostDraw runs with drawBackground off for the call: the quad is left out, the
    //    occupant VEF draws still shows.
    // Only while Api.ModelStandsIn(thing): without a model, or with the 3D view off, VEF draws as it does. VEF is reached by
    // name; a missing type or field leaves that part as VEF draws it (logged once).
    internal static class Models3D
    {
        internal static void Run(Harmony h)
        {
            try
            {
                Storage(h);
            }
            catch (Exception e)
            {
                Log.Error(PackMain.Tag + "Pipe storage bars (they stay flat under 3D models): " + e);
            }
            try
            {
                Biosculpter(h);
            }
            catch (Exception e)
            {
                Log.Error(PackMain.Tag + "Biosculpter pod (its flat background may show beside the 3D pod): " + e);
            }
        }

        // ---- PipeSystem storage fill bar
        private static AccessTools.FieldRef<object, GenDraw.FillableBarRequest> barRequest;
        private static Vector3 barCenter;   // the bar's own centre while it is drawn over the middle (main thread)

        private static void Storage(Harmony h)
        {
            Type storage = AccessTools.TypeByName("PipeSystem.CompResourceStorage");
            MethodInfo draw = storage != null ? AccessTools.DeclaredMethod(storage, "PostDraw") : null;
            FieldInfo request = storage != null ? AccessTools.Field(storage, "request") : null;
            if (draw == null || request == null || request.FieldType != typeof(GenDraw.FillableBarRequest))
            {
                if (storage != null)
                {
                    Log.Warning(PackMain.Tag + "PipeSystem's storage bar not found: storage bars stay flat under 3D models.");
                }
                return;
            }
            barRequest = AccessTools.FieldRefAccess<GenDraw.FillableBarRequest>(storage, "request");
            h.Patch(draw, prefix: new HarmonyMethod(typeof(Models3D), nameof(BarPostDraw_Prefix)),
                finalizer: new HarmonyMethod(typeof(Models3D), nameof(BarPostDraw_Finalizer)));
        }

        public static void BarPostDraw_Prefix(ThingComp __instance, out bool __state)
        {
            __state = false;
            Thing t = __instance?.parent;
            if (t != null && IsoRim.Api.ModelStandsIn(t))
            {
                ref GenDraw.FillableBarRequest req = ref barRequest(__instance);
                barCenter = req.center;
                Vector3 mid = t.DrawPos;
                req.center = new Vector3(mid.x, barCenter.y, mid.z);
                IsoRim.Api.BeginUpright(IsoRim.Api.ModelHeight(t.def) + 0.3f);   // hinged on the bar, now over the middle
                __state = true;
            }
        }

        public static Exception BarPostDraw_Finalizer(ThingComp __instance, Exception __exception, bool __state)
        {
            if (__state)
            {
                IsoRim.Api.EndUpright();
                barRequest(__instance).center = barCenter;
            }
            return __exception;
        }

        // ---- VEF's extended biosculpter pod
        private static FieldInfo drawBackground;

        private static void Biosculpter(Harmony h)
        {
            Type comp = AccessTools.TypeByName("VEF.Buildings.CompExtendedBiosculpterPod");
            Type props = AccessTools.TypeByName("VEF.Buildings.CompProperties_ExtendedBiosculpterPod");
            MethodInfo draw = comp != null ? AccessTools.DeclaredMethod(comp, "PostDraw") : null;
            drawBackground = props != null ? AccessTools.Field(props, "drawBackground") : null;
            if (draw == null || drawBackground == null || drawBackground.FieldType != typeof(bool))
            {
                if (comp != null)
                {
                    Log.Warning(PackMain.Tag + "VEF's biosculpter pod background not found: it may show beside 3D pods.");
                }
                return;
            }
            h.Patch(draw, prefix: new HarmonyMethod(typeof(Models3D), nameof(PodPostDraw_Prefix)),
                finalizer: new HarmonyMethod(typeof(Models3D), nameof(PodPostDraw_Finalizer)));
        }

        // The props are shared by every pod of the def: the flag is off for this call only (main thread).
        public static void PodPostDraw_Prefix(ThingComp __instance, out bool __state)
        {
            __state = false;
            if (__instance?.props != null && IsoRim.Api.ModelStandsIn(__instance.parent) && (bool)drawBackground.GetValue(__instance.props))
            {
                drawBackground.SetValue(__instance.props, false);
                __state = true;
            }
        }

        public static Exception PodPostDraw_Finalizer(ThingComp __instance, Exception __exception, bool __state)
        {
            if (__state)
            {
                drawBackground.SetValue(__instance.props, true);
            }
            return __exception;
        }
    }
}
