using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace TabSorting;

/// <summary>
///     Decides if an architect tab should be hidden: either because it is empty (old HideEmptyTabs behaviour),
///     or because nothing in it is available yet, i.e. every buildable still needs unfinished research or is
///     outside the colony/world tech level (HideTechLockedTabs behaviour).
/// </summary>
public static class TabVisibility
{
    private const float CacheSeconds = 1f;

    private static readonly Dictionary<DesignationCategoryDef, bool> techLockedCache = new();
    private static float cacheStamp = -999f;
    private static TechLevel cachedColonyTech = TechLevel.Undefined;
    private static TechLevel cachedResearchLevel = TechLevel.Undefined;
    private static bool cachedWorldTechActive;
    private static HashSet<Type> sharedDesignatorTypes;
    private static int sharedTypesCategoryCount = -1;

    public static bool AnyHidingEnabled
    {
        get
        {
            var settings = TabSortingMod.Instance.Settings;
            return settings.HideEmptyTabs || settings.HideTechLockedTabs;
        }
    }

    public static bool ShouldHide(ArchitectCategoryTab tab)
    {
        var settings = TabSortingMod.Instance.Settings;
        if (settings.HideEmptyTabs && !tab.Visible)
        {
            return true;
        }

        return settings.HideTechLockedTabs && IsLockedByTechLevel(tab.def);
    }

    public static void ClearCache()
    {
        techLockedCache.Clear();
        cacheStamp = -999f;
        sharedDesignatorTypes = null;
    }

    public static bool IsLockedByTechLevel(DesignationCategoryDef category)
    {
        if (category == null || Faction.OfPlayer?.def == null)
        {
            return false;
        }

        // Dev mode "god mode" shows everything, same as vanilla.
        if (DebugSettings.godMode)
        {
            return false;
        }

        // Tech level state is read only when the cache expires (about once per second), not per tab per pass.
        if (Time.realtimeSinceStartup - cacheStamp > CacheSeconds)
        {
            techLockedCache.Clear();
            cacheStamp = Time.realtimeSinceStartup;
            cachedColonyTech = Faction.OfPlayer.def.techLevel;
            cachedWorldTechActive = WorldTechLevelCompat.Active;
            cachedResearchLevel = cachedWorldTechActive
                ? WorldTechLevelCompat.PlayerResearchFilterLevel()
                : cachedColonyTech;
        }

        if (techLockedCache.TryGetValue(category, out var cached))
        {
            return cached;
        }

        var result = calculateLockedByTechLevel(category, cachedColonyTech, cachedResearchLevel,
            cachedWorldTechActive);
        techLockedCache[category] = result;
        return result;
    }

    /// <summary>
    ///     A tab is hidden when none of its designators is usable right now. Anything that is not a build
    ///     designator (orders, zones, planning...) counts as usable, so those tabs always stay.
    /// </summary>
    private static bool calculateLockedByTechLevel(DesignationCategoryDef category, TechLevel colonyTech,
        TechLevel researchLevel, bool worldTechActive)
    {
        var designators = category.AllResolvedDesignators;
        if (designators == null || designators.Count == 0)
        {
            return false;
        }

        var flattened = new List<Designator>();
        foreach (var designator in designators)
        {
            flatten(designator, flattened);
        }

        foreach (var designator in flattened)
        {
            // Already hidden by the game itself, does not count as available.
            if (!designator.Visible)
            {
                continue;
            }

            // Designators shared with other tabs (Cancel, Deconstruct...) must not keep a tab alive.
            if (isUtilityDesignator(designator))
            {
                continue;
            }

            if (designator is not Designator_Build buildDesignator)
            {
                return false;
            }

            if (isBuildableAvailable(buildDesignator.PlacingDef, colonyTech, researchLevel, worldTechActive))
            {
                return false;
            }
        }

        return true;
    }

    private static readonly HashSet<string> utilityDesignatorTypeNames =
    [
        "Designator_Cancel",
        "Designator_Deconstruct",
        "Designator_Uninstall",
        "Designator_Install",
        "Designator_ReplaceStuff"
    ];

    /// <summary>
    ///     A non-build designator is "shared" if the same designator type is offered in more than one tab
    ///     (Cancel, Deconstruct, ... which are also in the Orders tab). Those never keep a tab alive.
    /// </summary>
    private static bool isUtilityDesignator(Designator designator)
    {
        for (var type = designator.GetType(); type != null && type != typeof(Designator); type = type.BaseType)
        {
            if (utilityDesignatorTypeNames.Contains(type.Name))
            {
                return true;
            }
        }

        return getSharedDesignatorTypes().Contains(designator.GetType());
    }

    private static HashSet<Type> getSharedDesignatorTypes()
    {
        var categories = DefDatabase<DesignationCategoryDef>.AllDefsListForReading;
        if (sharedDesignatorTypes != null && sharedTypesCategoryCount == categories.Count)
        {
            return sharedDesignatorTypes;
        }

        var tabsPerType = new Dictionary<Type, int>();
        var flattened = new List<Designator>();
        foreach (var category in categories)
        {
            var designators = category.AllResolvedDesignators;
            if (designators == null)
            {
                continue;
            }

            flattened.Clear();
            foreach (var designator in designators)
            {
                flatten(designator, flattened);
            }

            foreach (var type in flattened.Where(designator => designator is not Designator_Build)
                         .Select(designator => designator.GetType()).Distinct())
            {
                tabsPerType[type] = tabsPerType.TryGetValue(type, out var count) ? count + 1 : 1;
            }
        }

        sharedDesignatorTypes = tabsPerType.Where(pair => pair.Value > 1).Select(pair => pair.Key).ToHashSet();
        sharedTypesCategoryCount = categories.Count;
        return sharedDesignatorTypes;
    }

    private static void flatten(Designator designator, List<Designator> result)
    {
        if (designator is Designator_Dropdown dropdown && dropdown.Elements != null)
        {
            foreach (var element in dropdown.Elements)
            {
                flatten(element, result);
            }

            return;
        }

        result.Add(designator);
    }

    /// <summary>
    ///     Available means: allowed by the tech level and all research prerequisites are finished.
    /// </summary>
    private static bool isBuildableAvailable(BuildableDef def, TechLevel colonyTech, TechLevel researchLevel,
        bool worldTechActive)
    {
        if (def == null)
        {
            return true;
        }

        if (def.minTechLevelToBuild != TechLevel.Undefined && colonyTech < def.minTechLevelToBuild)
        {
            return false;
        }

        if (def.maxTechLevelToBuild != TechLevel.Undefined && colonyTech > def.maxTechLevelToBuild)
        {
            return false;
        }

        var prerequisites = def.researchPrerequisites;
        var hasPrerequisites = prerequisites is { Count: > 0 };
        if (hasPrerequisites)
        {
            // Research is the gate: finished means available, anything else means not yet.
            return prerequisites.All(project => project == null || project.IsFinished);
        }

        // No research needed, but World Tech Level may still consider the item above the allowed level.
        return !worldTechActive || WorldTechLevelCompat.MinRequiredTechLevel(def) <= researchLevel;
    }

}

/// <summary>
///     Optional integration with the "World Tech Level" mod (by m00nl1ght), done via reflection so there is
///     no hard dependency. That mod keeps its own world tech level and does not change the player faction's one.
/// </summary>
internal static class WorldTechLevelCompat
{
    private static readonly bool loaded;
    private static readonly MethodInfo playerFilterLevelMethod;
    private static readonly MethodInfo minRequiredGenericDefinition;
    private static readonly FieldInfo settingsField;
    private static readonly Dictionary<Type, MethodInfo> closedMethods = new();

    static WorldTechLevelCompat()
    {
        try
        {
            var utilityType = AccessTools.TypeByName("WorldTechLevel.TechLevelUtility");
            if (utilityType == null)
            {
                return;
            }

            playerFilterLevelMethod = utilityType.GetMethod("PlayerResearchFilterLevel",
                BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            minRequiredGenericDefinition = utilityType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method => method.Name == "MinRequiredTechLevel" &&
                                          method.IsGenericMethodDefinition &&
                                          method.GetParameters().Length == 1);
            settingsField = AccessTools.TypeByName("WorldTechLevel.WorldTechLevel")?.GetField("Settings",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            loaded = playerFilterLevelMethod != null && minRequiredGenericDefinition != null;
            TabSorting.LogMessage($"World Tech Level integration for tab hiding available: {loaded}", true);
        }
        catch (Exception exception)
        {
            TabSorting.LogMessage($"Failed to set up World Tech Level integration.\n{exception}", true);
            loaded = false;
        }
    }

    /// <summary>
    ///     True if World Tech Level is loaded and its research filter is on.
    /// </summary>
    public static bool Active
    {
        get
        {
            if (!loaded)
            {
                return false;
            }

            try
            {
                var settings = settingsField?.GetValue(null);
                var entry = settings?.GetType().GetField("Filter_Research")?.GetValue(settings);
                var value = entry?.GetType().GetProperty("Value")?.GetValue(entry);
                return value is not bool enabled || enabled;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    public static TechLevel PlayerResearchFilterLevel()
    {
        return (TechLevel)playerFilterLevelMethod.Invoke(null, null);
    }

    /// <summary>
    ///     The minimum tech level the mod says this def requires. Undefined if unknown.
    /// </summary>
    public static TechLevel MinRequiredTechLevel(Def def)
    {
        if (!loaded || def == null)
        {
            return TechLevel.Undefined;
        }

        // The mod's database is keyed by the static type, so use the exact types it knows about.
        var type = def switch
        {
            ResearchProjectDef => typeof(ResearchProjectDef),
            ThingDef => typeof(ThingDef),
            TerrainDef => typeof(TerrainDef),
            _ => null
        };
        if (type == null)
        {
            return TechLevel.Undefined;
        }

        try
        {
            if (!closedMethods.TryGetValue(type, out var method))
            {
                method = minRequiredGenericDefinition.MakeGenericMethod(type);
                closedMethods[type] = method;
            }

            return (TechLevel)method.Invoke(null, [def]);
        }
        catch (Exception)
        {
            return TechLevel.Undefined;
        }
    }
}
