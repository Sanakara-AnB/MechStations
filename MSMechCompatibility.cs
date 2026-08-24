using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;

namespace MechStations
{
    /// <summary>
    /// Works out once at startup which mechs can actually use our ports, and
    /// reports what it found to the log.
    /// </summary>
    // Two questions have to be answered yes, and the second is the one that is
    // easy to miss:
    //
    //   1. Can the player ever own this mech? A recipe has to produce it.
    //      Resurrection does NOT count as a second route: both the mechanitor
    //      ability and the gestator recipe only accept FRIENDLY corpses
    //      (Faction check in CompAbilityEffect_ResurrectMech, and the
    //      AllowCorpsesMechFriendly filter on the recipe), so they restore a
    //      mech the player already had - they never grant a new kind.
    //
    //   2. Can it reach a port on its own? Our docking givers live in the think
    //      tree, and the XML patch only reaches the trees it names. A mod mech
    //      with its own tree - GD5's Mosquito copies the whole vanilla tree for
    //      its combat subtrees - charges happily at vanilla rechargers and
    //      never sees our ports, because nobody inserted our branch there.
    //
    // The weight class is deliberately NOT checked here: it is per port, not
    // per mod, and MSCompMechDocking asks it against its own allowedWeightClasses.
    [StaticConstructorOnStartup]
    public static class MSMechCompatibility
    {
        // Maps a buildable mech to the mod whose RECIPE makes it buildable -
        // which is not always the mod that defines the mech. DMS_Mech_Lady is
        // declared by the DMS core but only gets a gestator recipe from the
        // Synthetic add-on; without that add-on she is not obtainable at all.
        // The recipe's mod is therefore the one that "causes" an entry, and
        // the one a player would recognise as the culprit.
        //
        // First recipe wins if several mods produce the same mech.
        private static readonly Dictionary<ThingDef, ModContentPack> _buildable
            = new Dictionary<ThingDef, ModContentPack>();

        private static readonly HashSet<ThingDef> _reachable = new HashSet<ThingDef>();

        // Every weight class any of our ports will take, read from the ports'
        // own XML rather than listing the four vanilla classes here. Today the
        // union is exactly those four, but a fifth port, a changed allocation
        // or an outside compat patch would silently make a hardcoded list lie.
        private static readonly HashSet<MechWeightClassDef> _acceptedClasses
            = new HashSet<MechWeightClassDef>();

        // A port with no list accepts everything (see Accepts), so one such
        // port disables the whole check rather than widening the set.
        private static bool _anyPortAcceptsAll;

        static MSMechCompatibility()
        {
            CollectBuildable();
            CollectReachable();
            CollectAcceptedClasses();
            LogSummary();
        }

        /// <summary>
        /// Whether a mech can end up under player control at all.
        /// </summary>
        public static bool IsBuildable(ThingDef race) =>
            race != null && _buildable.ContainsKey(race);

        /// <summary>
        /// The mod whose recipe makes this mech buildable, or null.
        /// </summary>
        public static ModContentPack RecipeModOf(ThingDef race) =>
            race != null && _buildable.TryGetValue(race, out ModContentPack m)
                ? m
                : null;

        /// <summary>
        /// Whether a mech's think tree carries our docking branch, i.e. whether
        /// it can ever walk to a port by itself.
        /// </summary>
        public static bool CanReachPorts(ThingDef race) =>
            race != null && _reachable.Contains(race);

        /// <summary>
        /// Whether any port would take this mech's weight class at all.
        /// </summary>
        // Deliberately asks EffectiveClassFor and not RaceProps.mechWeightClass:
        // an MSMechClassOverrideDef can map an unusable class onto an accepted
        // one, and a mech someone already fixed that way must not be reported
        // as broken. A mech with no class at all fails here too - it is in no
        // list - which also covers vanilla's "mechWeightClass != null" rule.
        public static bool IsAcceptedByAnyPort(ThingDef race)
        {
            if (_anyPortAcceptsAll) return true;

            MechWeightClassDef cls = MSMechClassOverrides.EffectiveClassFor(race);
            return cls != null && _acceptedClasses.Contains(cls);
        }

        /// <summary>
        /// All three of the above. What the port's compatibility list should show.
        /// </summary>
        public static bool IsUsable(ThingDef race) =>
            IsBuildable(race) && CanReachPorts(race) && IsAcceptedByAnyPort(race);

        // A gestator recipe names its mech in <products>.
        private static void CollectBuildable()
        {
            foreach (RecipeDef recipe in DefDatabase<RecipeDef>.AllDefs)
            {
                if (recipe.products == null) continue;

                for (int i = 0; i < recipe.products.Count; i++)
                {
                    ThingDef product = recipe.products[i].thingDef;
                    if (product?.race == null || !product.race.IsMechanoid)
                        continue;

                    if (!_buildable.ContainsKey(product))
                        _buildable[product] = recipe.modContentPack;
                }
            }
        }

        private static void CollectAcceptedClasses()
        {
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                MSCompProperties_MechDocking props =
                    def.GetCompProperties<MSCompProperties_MechDocking>();
                if (props == null) continue;

                if (props.allowedWeightClasses.NullOrEmpty())
                {
                    _anyPortAcceptsAll = true;
                    continue;
                }

                for (int i = 0; i < props.allowedWeightClasses.Count; i++)
                {
                    MechWeightClassDef cls = props.allowedWeightClasses[i];
                    if (cls != null) _acceptedClasses.Add(cls);
                }
            }
        }

        private static void CollectReachable()
        {
            HashSet<ThinkTreeDef> patched = new HashSet<ThinkTreeDef>();

            foreach (ThinkTreeDef tree in DefDatabase<ThinkTreeDef>.AllDefs)
            {
                if (tree.thinkRoot != null && CarriesDockingBranch(tree.thinkRoot))
                    patched.Add(tree);
            }

            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                if (def.race == null || !def.race.IsMechanoid) continue;
                if (def.race.thinkTreeMain != null
                    && patched.Contains(def.race.thinkTreeMain))
                    _reachable.Add(def);
            }
        }

        // Plain recursion is enough: ThinkNode_Subtree.ResolveSubnodes puts the
        // referenced tree's root into its OWN subNodes (verified via decompile),
        // so a branch sitting in a subtree is reachable from here.
        private static bool CarriesDockingBranch(ThinkNode node)
        {
            if (node is MSJobGiver_DockingMode || node is MSJobGiver_DockingReturn)
                return true;

            if (node.subNodes == null) return false;

            for (int i = 0; i < node.subNodes.Count; i++)
            {
                if (CarriesDockingBranch(node.subNodes[i])) return true;
            }
            return false;
        }

        // One summary line always, and a detail list only for the group that
        // needs action. The third group - mechs the player can never own - is a
        // number only; listing it would bury the useful part.
        private static void LogSummary()
        {
            int usable = 0;
            List<ThingDef> wrongClass = new List<ThingDef>();
            List<ThingDef> unreachable = new List<ThingDef>();
            int raidOnly = 0;

            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                if (def.race == null || !def.race.IsMechanoid) continue;

                // Only mechanitor candidates are counted at all. IsMechanoid
                // alone just means "made of mechanoid flesh" and sweeps in
                // drones and boss mechs by the hundred, which drowns the
                // numbers that matter. An overseer comp means the mech COULD
                // belong to a mechanitor - it does not mean it ever will,
                // which is what the buildable check below decides.
                if (def.GetCompProperties<CompProperties_OverseerSubject>() == null)
                    continue;

                if (!IsBuildable(def)) { raidOnly++; continue; }

                // Weight class before think tree, deliberately. If no port
                // takes the class, patching the tree changes nothing - the
                // mech would walk to a port that then refuses it. Reporting
                // it as "tree not patched" would send the reader after the
                // wrong fix.
                if (!IsAcceptedByAnyPort(def)) { wrongClass.Add(def); continue; }

                if (!CanReachPorts(def)) { unreachable.Add(def); continue; }
                usable++;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append($"[MechStations] {usable} usable mechs, ");
            sb.Append($"{wrongClass.Count} wrong weight class, ");
            sb.Append($"{unreachable.Count} buildable but unreachable, ");
            // Line break, not a space: the log list shows only the FIRST line
            // of an entry, so the parenthetical belongs below it rather than
            // padding the one line a reader skims.
            sb.AppendLine($"{raidOnly} raid-only.");
            sb.Append("(Counting mechanitor candidates only - drones and "
                + "mechs without an overseer comp are not listed.)");

            // Yellow only when there is something to act on. A status line that
            // always warns trains the reader to ignore it, and it makes other
            // people's bug reports harder to read - a yellow entry should mean
            // "look here", not "the mod loaded".
            if (wrongClass.Count == 0 && unreachable.Count == 0)
            {
                Log.Message(sb.ToString());
                return;
            }

            if (wrongClass.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine($"{wrongClass.Count} buildable mechs carry a "
                    + "weight class no port accepts.");

                AppendClassGroups(sb, wrongClass);
            }

            if (unreachable.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine();

                // Deliberately NOT "will never reach a port". Charging is not
                // affected: our postfix on JobGiver_GetEnergy_Charger hangs on
                // the CLASS, so it works in every think tree that contains that
                // giver. What these mechs lack is the MS_Docking work mode and
                // the return home when idle, both of which live in the tree.
                // The claim holds because a mech whose class no port accepts
                // was already sorted into the group above.
                sb.AppendLine($"{unreachable.Count} buildable mechs cannot use "
                    + "the MS_Docking work mode and will not return to an "
                    + "assigned port when idle. They DO still charge at ports "
                    + "when their energy runs low.");

                AppendGroups(sb, unreachable);
            }

            Log.Warning(sb.ToString());
        }

        private const int NamesShown = 5;

        // Grouped by the mod whose RECIPE makes the mech buildable - not by the
        // mod that defines the mech, and not by think tree.
        //
        // Two reasons. One tree can serve mechs from several mods, and a player
        // running only one of them needs to see HIS mod named rather than a
        // tree he has never heard of. And the recipe's mod is the one that
        // actually causes the entry: DMS_Mech_Lady is declared by the DMS core,
        // but without the Synthetic add-on she has no recipe, is not
        // obtainable, and would never appear here at all. Grouping her under
        // the core would point the reader at the wrong mod.
        //
        // The tree is named per group as the thing that would need patching.
        private static void AppendGroups(StringBuilder sb, List<ThingDef> mechs)
        {
            Dictionary<string, List<ThingDef>> groups =
                new Dictionary<string, List<ThingDef>>();

            foreach (ThingDef def in mechs)
            {
                string key = SourceModNameOf(def) + "\n" + TreeNameOf(def);
                if (!groups.TryGetValue(key, out List<ThingDef> list))
                {
                    list = new List<ThingDef>();
                    groups[key] = list;
                }
                list.Add(def);
            }

            foreach (KeyValuePair<string, List<ThingDef>> group in
                     groups.OrderBy(g => g.Key))
            {
                List<ThingDef> list = group.Value;
                ThingDef first = list[0];

                string sourceMod = SourceModNameOf(first);
                string treeMod = TreeModNameOf(first);
                string origin = treeMod == sourceMod
                    ? "from this mod"
                    : "from: " + treeMod;

                sb.AppendLine();
                sb.AppendLine($"{sourceMod}:");
                sb.AppendLine($"  ThinkTree: \"{TreeNameOf(first)}\"  "
                    + $"[{origin}]  - {list.Count} "
                    + (list.Count == 1 ? "mech" : "mechs"));

                // Label AND defName: a player reports "the Lady will not
                // dock", a modder needs DMS_Mech_Lady. Both sides can read it.
                IEnumerable<string> names = list
                    .OrderBy(d => d.LabelCap.Resolve())
                    .Take(NamesShown)
                    .Select(d => $"{d.LabelCap.Resolve()} [{d.defName}]");

                string line = "  " + string.Join(", ", names);
                if (list.Count > NamesShown)
                    line += $" (and {list.Count - NamesShown} more)";

                sb.AppendLine(line);
            }
        }

        // Same grouping by recipe mod as AppendGroups, but no think tree line:
        // here the class is the problem, and naming a tree would point at the
        // wrong fix. The class is printed per mech instead - one mod can ship
        // several, and the reader needs the exact name for an override def.
        private static void AppendClassGroups(StringBuilder sb, List<ThingDef> mechs)
        {
            Dictionary<string, List<ThingDef>> groups =
                new Dictionary<string, List<ThingDef>>();

            foreach (ThingDef def in mechs)
            {
                string key = SourceModNameOf(def);
                if (!groups.TryGetValue(key, out List<ThingDef> list))
                {
                    list = new List<ThingDef>();
                    groups[key] = list;
                }
                list.Add(def);
            }

            foreach (KeyValuePair<string, List<ThingDef>> group in
                     groups.OrderBy(g => g.Key))
            {
                List<ThingDef> list = group.Value;

                sb.AppendLine();
                sb.AppendLine($"{group.Key}:  - {list.Count} "
                    + (list.Count == 1 ? "mech" : "mechs"));

                IEnumerable<string> names = list
                    .OrderBy(d => d.LabelCap.Resolve())
                    .Take(NamesShown)
                    .Select(d => $"{d.LabelCap.Resolve()} [{d.defName}] "
                        + $"({ClassNameOf(d)})");

                string line = "  " + string.Join(", ", names);
                if (list.Count > NamesShown)
                    line += $" (and {list.Count - NamesShown} more)";

                sb.AppendLine(line);
            }
        }

        private static string ClassNameOf(ThingDef def) =>
            MSMechClassOverrides.EffectiveClassFor(def)?.defName ?? "none";

        private static string ModNameOf(Def def) =>
            def?.modContentPack?.Name ?? "unknown mod";

        // The mod that makes the mech OBTAINABLE, which is what a reader can
        // act on. Falls back to the defining mod if no recipe was recorded -
        // that cannot happen for anything in this list, since being buildable
        // is what puts a mech here in the first place.
        private static string SourceModNameOf(ThingDef def) =>
            RecipeModOf(def)?.Name ?? ModNameOf(def);

        private static string TreeNameOf(ThingDef def) =>
            def.race?.thinkTreeMain?.defName ?? "none";

        private static string TreeModNameOf(ThingDef def) =>
            ModNameOf(def.race?.thinkTreeMain);
    }
}
