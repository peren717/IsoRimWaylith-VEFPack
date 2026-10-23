using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PipeSystem;
using ProjectZ;
using UnityEngine;
using Verse;

namespace IsoRimVEFPackZ
{
    // The pipe rule, the patches and the drawing (see PackZ).
    public static class PipeRisers
    {
        // Every pure pipe def of every PipeNetDef (PipeNetDef.pipeDefs).
        internal static readonly HashSet<ThingDef> Pipes = new HashSet<ThingDef>();

        private static readonly Dictionary<PipeNetDef, PipeRiserSource> sources = new Dictionary<PipeNetDef, PipeRiserSource>();

        // The net whose overlay VEF draws now (SectionLayer_Resource.UpdateAndDrawFor, asked every frame while shown).
        internal static PipeNetDef shownDef;
        internal static int shownFrame = -10;

        // Tests: how many stacked pipes the neighbour patch added in all.
        public static int Added;

        private static readonly List<IntVec3> stacked = new List<IntVec3>();

        internal static void Init()
        {
            List<PipeNetDef> nets = DefDatabase<PipeNetDef>.AllDefsListForReading;
            for (int i = 0; i < nets.Count; i++)
            {
                List<ThingDef> defs = nets[i].pipeDefs;
                if (defs == null)
                {
                    continue;
                }
                for (int j = 0; j < defs.Count; j++)
                {
                    if (defs[j] != null)
                    {
                        Pipes.Add(defs[j]);
                    }
                }
            }
        }

        internal static void AddSources()
        {
            List<PipeNetDef> nets = DefDatabase<PipeNetDef>.AllDefsListForReading;
            for (int i = 0; i < nets.Count; i++)
            {
                if (nets[i].pipeDefs.NullOrEmpty() || sources.ContainsKey(nets[i]))
                {
                    continue;
                }
                PipeRiserSource s = new PipeRiserSource(nets[i]);
                sources[nets[i]] = s;
                Api.AddRiserSource(s);
            }
        }

        // The source drawing this net's risers (tests).
        public static RiserSource SourceFor(PipeNetDef def) => def != null && sources.TryGetValue(def, out PipeRiserSource s) ? s : null;

        internal static void MarkDirty(Map map, PipeNetDef def)
        {
            if (map != null && def != null && sources.TryGetValue(def, out PipeRiserSource s))
            {
                s.MarkDirty(map);
            }
        }

        internal static bool IsPipe(ThingDef def) => def != null && Pipes.Contains(def);

        // The transmitting pipe of net 'def' in cell c, with its comp; null if none.
        internal static CompResource PipeCompAt(IntVec3 c, Map map, PipeNetDef def)
        {
            if (!c.IsValid || !c.InBounds(map))
            {
                return null;
            }
            List<Thing> list = map.thingGrid.ThingsListAt(c);
            for (int i = 0; i < list.Count; i++)
            {
                if (!IsPipe(list[i].def) || !(list[i] is ThingWithComps t))
                {
                    continue;
                }
                List<ThingComp> comps = t.AllComps;
                for (int j = 0; j < comps.Count; j++)
                {
                    if (comps[j] is CompResource r && r.Props.pipeNet == def && r.TransmitResourceNow)
                    {
                        return r;
                    }
                }
            }
            return null;
        }

        // The joining rule (postfix of PipeNetManager.NeighbourThingsCardinal, the neighbour list of all three of VEF's net
        // builders): for a pipe, the pipes in the stacked cells above and below each cell it occupies are added. The
        // callers then keep only comps of the right net that transmit. The manager's map is used: when a pipe is removed,
        // its thing is already despawned (Map null; Position and def still good).
        internal static void AddStacked(PipeNetManager manager, Thing thing, List<Thing> result)
        {
            Map map = manager?.map;
            if (result == null || thing == null || map == null || !IsPipe(thing.def) || !Api.HasFloors(map))
            {
                return;
            }
            foreach (IntVec3 c in GenAdj.OccupiedRect(thing.Position, thing.Rotation, thing.def.size))
            {
                stacked.Clear();
                Api.StackedCells(c, map, stacked);
                for (int k = 0; k < stacked.Count; k++)
                {
                    List<Thing> list = map.thingGrid.ThingsListAt(stacked[k]);
                    for (int i = 0; i < list.Count; i++)
                    {
                        Thing t = list[i];
                        if (t != thing && IsPipe(t.def) && !result.Contains(t))
                        {
                            result.Add(t);
                            Added++;
                        }
                    }
                }
            }
            stacked.Clear();
        }
    }

    // One net kind's vertical links, drawn by ProjectZ: a pipe with a pipe of its net (same PipeNet) stacked over it.
    internal sealed class PipeRiserSource : RiserSource
    {
        // VEF's invisible texture (its hidden conduits and pipes): no piece on such an end, as the game draws no sprite.
        private const string HiddenTex = "UI/CSG/IConduit";

        private readonly PipeNetDef def;
        private Material line, end;
        private bool materialsTried;
        private readonly Dictionary<ThingDef, Color32> colours = new Dictionary<ThingDef, Color32>();

        public PipeRiserSource(PipeNetDef def)
        {
            this.def = def;
        }

        public override void Collect(Map map, List<RiserLink> into)
        {
            PipeNetManager manager = CachedPipeNetManager.GetFor(map);
            if (manager == null)
            {
                return;
            }
            EnsureMaterials();
            List<PipeNet> nets = manager.pipeNets;
            for (int n = 0; n < nets.Count; n++)
            {
                PipeNet net = nets[n];
                if (net == null || net.def != def)
                {
                    continue;
                }
                for (int i = 0; i < net.connectors.Count; i++)
                {
                    CompResource c = net.connectors[i];
                    Thing p = c?.parent;
                    if (p == null || !p.Spawned || p.Map != map || !PipeRisers.IsPipe(p.def) || !c.TransmitResourceNow)
                    {
                        continue;
                    }
                    CompResource top = PipeRisers.PipeCompAt(Api.StackedCell(p.Position, map, true), map, def);
                    if (top == null || top.PipeNet != net)
                    {
                        continue;   // drawn only where VEF's nets really join (never disagrees with them)
                    }
                    bool lowDrawn = !Hidden(p.def), highDrawn = !Hidden(top.parent.def);
                    into.Add(new RiserLink
                    {
                        low = p.Position,
                        colour = ColourOf(lowDrawn || !highDrawn ? p : top.parent),   // a visible end's pipe
                        lowDrawn = lowDrawn,
                        highDrawn = highDrawn,
                        overlay = true,   // VEF's resource overlay shows every pipe, whoever owns it
                        overlayLine = line,
                        overlayEnd = end,
                    });
                }
            }
        }

        public override bool OverlayShown(Map map) => PipeRisers.shownDef == def && PipeRisers.shownFrame + 1 >= Time.frameCount;

        private static bool Hidden(ThingDef d) => d.drawerType == DrawerType.None || d.graphicData?.texPath == HiddenTex;

        // The net's own overlay tile (its transmitter atlas, tinted with overlayColor), linked up and down; its unlinked tile
        // as the end mark.
        private void EnsureMaterials()
        {
            if (materialsTried)
            {
                return;
            }
            materialsTried = true;
            try
            {
                Material atlas = LinkedPipes.GetOverlayFor(def)?.SubGraphic?.MatSingle;
                if (atlas != null)
                {
                    line = MaterialAtlasPool.SubMaterialFromAtlas(atlas, LinkDirections.Up | LinkDirections.Down);
                    end = MaterialAtlasPool.SubMaterialFromAtlas(atlas, LinkDirections.None);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PackZ.Tag + $"No overlay tile for {def.defName}: " + e, def.shortHash ^ 0x7e9a11);
            }
        }

        // The pipe's sprite colour (IsoRim's mean sprite colour, Api 8), else the net's overlay colour.
        private Color32 ColourOf(Thing pipe)
        {
            if (!colours.TryGetValue(pipe.def, out Color32 c))
            {
                Color got = def.overlayOptions != null ? def.overlayOptions.overlayColor : Color.grey;
                try
                {
                    if (IsoRim.Api.Version >= 8)
                    {
                        got = Sprite(pipe);
                    }
                }
                catch (Exception e)
                {
                    Log.ErrorOnce(PackZ.Tag + $"No sprite colour for {pipe.def.defName}: " + e, pipe.def.shortHash ^ 0x7e9a12);
                }
                got.a = 1f;
                c = got;
                colours[pipe.def] = c;
            }
            return c;
        }

        private static Color Sprite(Thing pipe) => IsoRim.Api.SpriteColour(pipe);
    }

    // ---- patches (applied by Setup.Run through PatchAll(typeof(PackZ).Assembly); each skips itself when VEF changed)

    // The joining rule, in the neighbour list of RegisterConnector, UnregisterConnector and CreatePipeNetFrom.
    [HarmonyPatch]
    public static class Neighbours_Patch
    {
        private static MethodBase target;
        private static bool told;

        public static bool Prepare()
        {
            target = AccessTools.Method(typeof(PipeNetManager), "NeighbourThingsCardinal", new[] { typeof(Thing) });
            if (target == null || ((MethodInfo)target).ReturnType != typeof(List<Thing>))
            {
                if (!told)
                {
                    told = true;
                    Log.Warning(PackZ.Tag + "VEF's PipeNetManager.NeighbourThingsCardinal(Thing) not found: pipes stay on their own floor.");
                }
                target = null;
                return false;
            }
            return true;
        }

        public static MethodBase TargetMethod() => target;

        public static void Postfix(PipeNetManager __instance, Thing __0, List<Thing> __result)
        {
            try
            {
                PipeRisers.AddStacked(__instance, __0, __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PackZ.Tag + "Joining pipes across floors failed: " + e, 0x7e9a13);
            }
        }
    }

    // Every connector spawned, despawned or switched (valves) goes through these two: that net's risers are stale.
    [HarmonyPatch]
    public static class Connectors_Patch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo reg = AccessTools.Method(typeof(PipeNetManager), nameof(PipeNetManager.RegisterConnector));
            MethodInfo unreg = AccessTools.Method(typeof(PipeNetManager), nameof(PipeNetManager.UnregisterConnector));
            if (reg != null)
            {
                yield return reg;
            }
            if (unreg != null)
            {
                yield return unreg;
            }
        }

        public static void Postfix(PipeNetManager __instance, CompResource __0)
        {
            PipeNetDef def = __0?.Props?.pipeNet;
            if (def != null)
            {
                PipeRisers.MarkDirty(__instance?.map, def);
            }
        }
    }

    // Which net's overlay VEF shows this frame.
    [HarmonyPatch(typeof(SectionLayer_Resource), nameof(SectionLayer_Resource.UpdateAndDrawFor))]
    public static class Overlay_Patch
    {
        public static void Postfix(PipeNetDef pipeNetDef)
        {
            PipeRisers.shownDef = pipeNetDef;
            PipeRisers.shownFrame = Time.frameCount;
        }
    }
}
