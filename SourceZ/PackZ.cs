using System;
using HarmonyLib;
using Verse;

namespace IsoRimVEFPackZ
{
    // VEF PipeSystem pipe networks across IsoRimZ floors (needs ProjectZ Api 3; older: the pack does nothing, logged once).
    //  - Joining (PipeRisers.Neighbours_Patch): a pipe (a def in some PipeNetDef.pipeDefs) also touches the pipes in the
    //    stacked cells directly above and below it (ProjectZ.Api.StackedCell), the rule ProjectZ uses for power conduits.
    //    Every other PipeSystem building keeps VEF's horizontal-only adjacency. VEF's own net-type filter keeps nets of
    //    different PipeNetDefs apart.
    //  - Drawing (PipeRiserSource, one per PipeNetDef, ProjectZ.Api.AddRiserSource): each pipe with a pipe of its net over
    //    it is drawn by ProjectZ like a power riser, in the pipe's colour, with the net's own overlay tile while that net's
    //    overlay is shown.
    //  - Beams (Beams.cs): VEF's expandable beam projectiles (VWE's flamethrower) fired between floors start, draw and
    //    damage on the projectile's floor, as ProjectZ flies the shot, instead of across the flat map.
    // VEF is reached through its public types; its one private method (PipeNetManager.NeighbourThingsCardinal) and the beam
    // class by name: missing, that part is left as VEF does it (logged once).
    [StaticConstructorOnStartup]
    public static class PackZ
    {
        internal const string Tag = "[IsoRim VE Framework Pack, IsoRimZ] ";

        static PackZ()
        {
            try
            {
                if (ProjectZ.Api.Version < 3)
                {
                    Log.Message(Tag + "IsoRimZ has no stacked-cell API (Api 3): pipes stay on their own floor.");
                    return;
                }
                Setup.Run();
            }
            catch (Exception e)
            {
                Log.Error(Tag + "Setup failed (pipes stay on their own floor): " + e);
            }
        }
    }

    // Everything that names Api 3 types lives here, so an older ProjectZ never loads them.
    internal static class Setup
    {
        internal static void Run()
        {
            PipeRisers.Init();
            new Harmony("peren.isorimwaylith.vefpack.z").PatchAll(typeof(PackZ).Assembly);
            PipeRisers.AddSources();
        }
    }
}
