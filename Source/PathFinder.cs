using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;
using Unity.Collections;
using LudeonTK;

namespace PathfindingAvoidance;

[HarmonyPatch(typeof(PathFinderMapData))]
public static class PathFinderMapData_Patch
{
    // Handle attaching PathCostSource to PathFinderMapData.
    [HarmonyPostfix]
    [HarmonyPatch(MethodType.Constructor)]
    [HarmonyPatch(new Type[] { typeof( Map ) })]
    public static void Constructor( PathFinderMapData __instance, Map map )
    {
        PathCostSourceHandler handler = null;
        foreach( PathType pathType in Enum.GetValues( typeof( PathType )))
        {
            if( pathType.IsEnabled())
            {
                if( handler == null )
                    handler = PathCostSourceHandler.Get( __instance );
            }
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(Dispose))]
    public static void Dispose(PathFinderMapData __instance)
    {
        PathCostSourceHandler.RemoveMap( __instance );
    }

    // Propagate info about changed cells after updating sources.
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(GatherData))]
    public static IEnumerable<CodeInstruction> GatherData(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        FieldInfo cellDeltas = AccessTools.Field( typeof( PathFinderMapData ), "cellDeltas" );
        bool found = false;
        for( int i = 0; i < codes.Count; ++i )
        {
            // Log.Message("T:" + i + ":" + codes[i].opcode + "::" + (codes[i].operand != null ? codes[i].operand.ToString() : codes[i].operand));
            // The function has code:
            // cellDeltas.Clear();
            // After it (because the statement has a label that's jumped to), insert:
            // GatherData_Hook(this, cellDeltaSet, lastGatherTick);
            if( codes[ i ].opcode == OpCodes.Ldarg_0
                && i + 2 < codes.Count
                && codes[ i + 1 ].LoadsField( cellDeltas )
                && codes[ i + 2 ].opcode == OpCodes.Callvirt && codes[ i + 2 ].operand.ToString() == "Void Clear()" )

            {
                codes.Insert( i + 3, new CodeInstruction( OpCodes.Ldarg_0 )); // load 'this'
                codes.Insert( i + 4, new CodeInstruction( OpCodes.Ldarg_0 )); // for the next load
                codes.Insert( i + 5, CodeInstruction.LoadField( typeof( PathFinderMapData ), "cellDeltaSet" )); // load 'cellDeltaSet'
                codes.Insert( i + 6, new CodeInstruction( OpCodes.Ldarg_0 )); // for the next load
                codes.Insert( i + 7, CodeInstruction.LoadField( typeof( PathFinderMapData ), "lastGatherTick" )); // load 'lastGatherTick'
                codes.Insert( i + 8, new CodeInstruction( OpCodes.Call,
                    typeof( PathFinderMapData_Patch ).GetMethod( nameof( GatherData_Hook ))));
                found = true;
                break;
            }
        }
        if(!found)
            Log.Error( "PathfindingAvoidance: Failed to patch PathFinderMapData.GatherData()");
        return codes;
    }

    public static void GatherData_Hook( PathFinderMapData mapData, HashSet< IntVec3 > cellDeltasSet, int lastGatherTick )
    {
        // If 'lastGatherTick >= 0' is not true, everything was computed (and that overrides specific cells,
        // so some checks may be skipped).
        bool updateAll = !( lastGatherTick >= 0 );
        PathCostSourceHandler handler = PathCostSourceHandler.Get( mapData );
        if( !updateAll && handler.GetAllSources().Any( (PathCostSourceBase s) => s.AllChanged ))
            updateAll = true;
        Customizer.CellsNeedUpdate( mapData, cellDeltasSet, updateAll );
        if( !updateAll )
            foreach( PathCostSourceBase source in handler.GetAllSources())
                if( source.ExtraChangedCells.Count != 0 )
                    Customizer.CellsNeedUpdate( mapData, source.ExtraChangedCells, false );
        foreach( PathCostSourceBase source in handler.GetAllSources())
            source.ResetChanged();
    }

    // PathFinderMapData.ParameterizeGridJob() uses PathRequest.customizer instead of MapGridRequest.customizer.
    // It doesn't make a difference for vanilla, but it does not use our overriden customizer. Since conceptually
    // it seems incorrect to use the PathRequest one here (other things are read from MapGridRequest), simply fix that.
    [HarmonyTranspiler]
    [HarmonyPatch(nameof(ParameterizeGridJob))]
    public static IEnumerable<CodeInstruction> ParameterizeGridJob(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        FieldInfo pathRequestCustomizer = AccessTools.Field( typeof( PathRequest ), "customizer" );
        bool found = false;
        for( int i = 0; i < codes.Count; ++i )
        {
            // Log.Message("T:" + i + ":" + codes[i].opcode + "::" + (codes[i].operand != null ? codes[i].operand.ToString() : codes[i].operand));
            // The function has code:
            // request.customizer
            // Change to:
            // query.customizer
            if( codes[ i ].opcode == OpCodes.Ldarg_1 && i + 1 < codes.Count && codes[ i + 1 ].LoadsField( pathRequestCustomizer ))
            {
                codes[ i ] = new CodeInstruction( OpCodes.Ldarg_2 ).MoveLabelsFrom( codes[ i ] );
                codes[ i + 1 ] = CodeInstruction.LoadField( typeof( PathFinder.MapGridRequest ), "customizer" );
                found = true;
            }
        }
        if(!found)
            Log.Error( "PathfindingAvoidance: Failed to patch PathFinderMapData.ParameterizeGridJob()");
        return codes;
    }
}

// Need to override the customizer in created MapGridRequest objects.
[HarmonyPatch(typeof(PathFinder))]
public static class PathFinder_Patch
{
    // All these functions call MapGridRequest.Get(), and since that method does not have any reference to the outside
    // and we need PathFinderMapData, patch all callers.
    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> TargetMethod()
    {
        Type type = typeof(PathFinder);
        yield return AccessTools.Method(type, "ScheduleBatchedPathJobs");
        yield return AccessTools.Method(type, "ScheduleGridJobs");
        yield return AccessTools.Method(type, "ScheduleGridJob");
        yield return AccessTools.Method(type, "FindPathNow", new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms),
            typeof(PathFinderCostTuning?), typeof(PathEndMode), typeof(PathRequest.IPathGridCustomizer) } );
    }

    private static MethodInfo forMethod = AccessTools.Method(typeof(PathFinder.MapGridRequest), "For");

    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiller(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var codes = new List<CodeInstruction>(instructions);
        bool found = false;
        for( int i = 0; i < codes.Count; ++i )
        {
            // Log.Message("T:" + i + ":" + codes[i].opcode + "::" + (codes[i].operand != null ? codes[i].operand.ToString() : codes[i].operand));
            // The function has code:
            // MapGridRequest gridRequest = MapGridRequest.For(pathRequest);
            // Change to:
            // MapGridRequest gridRequest = Transpiler_Hook(MapGridRequest.For(pathRequest), pathRequest, this);
            if(( codes[ i ].IsLdloc() ||  codes[ i ].IsLdarg())
                 && i + 1 < codes.Count && codes[ i + 1 ].Calls( forMethod ))
            {
                codes.Insert( i + 2, codes[ i ].Clone()); // load 'pathRequest'
                codes.Insert( i + 3, new CodeInstruction( OpCodes.Ldarg_0 )); // load 'this'
                codes.Insert( i + 4, new CodeInstruction( OpCodes.Call, typeof(PathFinder_Patch).GetMethod(nameof(Transpiler_Hook))));
                found = true;
                break;
            }
        }
        if(!found)
            Log.Error( "PathfindingAvoidance: Failed to patch " + __originalMethod);
        return codes;
    }

    public static PathFinder.MapGridRequest Transpiler_Hook( PathFinder.MapGridRequest gridRequest, PathRequest pathRequest, PathFinder pathFinder )
    {
        Pawn pawn = pathRequest.pawn;
        PathType pathType = PathTypeUtils.GetPathType( pathRequest );
        if( pathType != PathType.None )
            gridRequest.customizer = Customizer.Get( pathType, pathFinder.mapData, gridRequest.customizer );
        return gridRequest;
    }
}

[HarmonyPatch(typeof(PathFinder))]
public static class PathFinder2_Patch
{
    // This is called when grids change, destroy our cached data.
    [HarmonyPostfix]
    [HarmonyPatch(nameof(RecycleGridJobData))]
    public static void RecycleGridJobData( PathFinder __instance )
    {
        Customizer.ClearMap( __instance.mapData );
    }
}
