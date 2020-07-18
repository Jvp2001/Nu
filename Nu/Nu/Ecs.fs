﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2020.

namespace Nu
open System
open System.Collections.Generic
open Prime

/// The base component type of an Ecs.
type Component =
    abstract RefCount : int with get, set

/// A storable reference to a component in its containing array.
type [<NoEquality; NoComparison; Struct>] ComponentRef<'t when 't : struct and 't :> Component> =
    { ComponentIndex : int
      ComponentArr : 't array }

    member this.Value
        with get () = &this.ComponentArr.[this.ComponentIndex]

    member this.Assign value =
        this.ComponentArr.[this.ComponentIndex] <- value

    static member (<!) (componentRef, value) =
        componentRef.ComponentArr.[componentRef.ComponentIndex] <- value

    static member (!>) componentRef =
        &componentRef.ComponentArr.[componentRef.ComponentIndex]

[<RequireQualifiedAccess>]
module ComponentRef =

    let make index arr =
        { ComponentIndex = index
          ComponentArr = arr }

/// Handle to one of an array of multiplexed components.
type Simplex<'t when 't : struct> =
    { mutable Simplex : 't }

/// Allows an entity to contain multiple of the same component.
/// However, it uses a dictionary without a small-object optimization, so this functionality won't get the typical
/// perf benefits of data-orientation. Really, this functionality is here for flexibility and convenience more than
/// anything else (which is good enough in almost all cases where multi-components are used).
type [<NoEquality; NoComparison; Struct>] ComponentMultiplexed<'t when 't : struct> =
    internal
        { mutable RefCount : int
          Entries : Dictionary<Guid, 't Simplex> }
    interface Component with
        member this.RefCount
            with get () = this.RefCount
            and set value = this.RefCount <- value
    member this.Components
        with get () = this.Entries.Values :> 't Simplex IEnumerable
    member this.RegisterComponent (multiId, comp) =
        this.Entries.Add (multiId, { Simplex = comp })
    member this.UnregisterComponent multiId =
        this.Entries.Remove multiId

/// A base system type of an Ecs.
type [<AbstractClass>] 'w System () =
    abstract Update : 'w Ecs -> 'w -> 'w
    default this.Update _ world = world
    abstract PostUpdate : 'w Ecs -> 'w -> 'w
    default this.PostUpdate _ world = world
    abstract Actualize : 'w Ecs -> 'w -> 'w
    default this.Actualize _ world = world

/// Nu's custom Entity-Component-System implementation.
/// While this isn't the most efficient ECS, it isn't the least efficient either. Due to the set-associative nature of
/// modern caches, most cache hits will be of the L2 variety for junctioned components. Uncorrelated components will be
/// L2-bound as is typical. Degradation of cache-prediction would only occur when a significant number of junctioned
/// components are very chaotically unregistered in a use-case scenario that the I, the library author, have trouble
/// even imagining.
and [<NoEquality; NoComparison>] 'w Ecs () =

    let systemsUnordered = dictPlus [] : Dictionary<string, 'w System>
    let systemsOrdered = List () : (string * 'w System) List
    let correlations = dictPlus [] : Dictionary<Guid, string List>

    member internal this.Correlations 
        with get () = correlations

    member this.Update world =
        Seq.fold
            (fun world (_, system : 'w System) -> system.Update this world)
            world systemsOrdered

    member this.PostUpdate world =
        Seq.fold
            (fun world (_, system : 'w System) -> system.PostUpdate this world)
            world systemsOrdered

    member this.Actualize world =
        Seq.fold
            (fun world (_, system : 'w System) -> system.Actualize this world)
            world systemsOrdered

    member this.AddSystem systemName system =
        systemsUnordered.Add (systemName, system)
        systemsOrdered.Add (systemName, system)

    member this.RemoveSystem systemName =
        systemsOrdered.RemoveAll (fun (systemName', _) -> systemName' = systemName) |> ignore
        systemsUnordered.Remove systemName |> ignore

    member this.TryGetSystem<'s when 's :> 'w System> systemName =
        match systemsUnordered.TryGetValue systemName with
        | (true, system) ->
            match system with
            | :? 's as systemAsS -> Some systemAsS
            | _ -> None
        | (false, _) -> None

    member this.GetSystem<'s when 's :> 'w System> systemName =
        this.TryGetSystem<'s> systemName |> Option.get

/// An Ecs system with just a single component.
and [<AbstractClass>] SystemSingleton<'t, 'w when 't : struct and 't :> Component> (comp : 't) =
    inherit System<'w> ()
    let mutable comp = comp
    member this.Component with get () = &comp
    type 'w Ecs with
        member this.GetComponentSingleton<'t, 'w when 't : struct and 't :> Component> systemName =
            let systemOpt = this.TryGetSystem<SystemSingleton<'t, 'w>> systemName 
            if Option.isNone systemOpt then failwith ("Could not find expected system '" + systemName + "'.")
            let system = Option.get systemOpt
            &system.Component

/// An Ecs system with components stored by a raw index.
/// Uncorrelated systems could potentially be processed in parallel.
type [<AbstractClass>] SystemUncorrelated<'t, 'w when 't : struct and 't :> Component> () =
    inherit System<'w> ()

    let mutable components = Array.zeroCreate 32 : 't array
    let mutable freeIndex = 0
    let freeList = Queue<int> ()

    member this.Components
        with get () = components
    
    member this.FreeIndex
        with get () = freeIndex

    member this.GetComponent index =
        if index >= freeIndex then raise (ArgumentOutOfRangeException "index")
        &components.[index]

    member this.AddComponent comp =
        if freeList.Count = 0 then
            if freeIndex < components.Length - 1 then
                components.[freeIndex] <- comp
                freeIndex <- inc freeIndex
            else
                let arr = Array.zeroCreate (components.Length * 2)
                components.CopyTo (arr, 0)
                components <- arr
                components.[freeIndex] <- comp
                freeIndex <- inc freeIndex
        else components.[freeList.Dequeue ()] <- comp

    member this.RemoveComponent index =
        if index <> freeIndex then
            components.[index].RefCount <- dec components.[index].RefCount
            if components.[index].RefCount = 0 then freeList.Enqueue index
        else freeIndex <- dec freeIndex

    type 'w Ecs with
    
        member this.GetComponent<'t when 't : struct and 't :> Component> systemName index =
            let systemOpt = this.TryGetSystem<SystemUncorrelated<'t, 'w>> systemName
            if Option.isNone systemOpt then failwith ("Could not find expected system '" + systemName + "'.")
            let system = Option.get systemOpt
            &system.GetComponent index
    
        member this.AddComponent<'t when 't : struct and 't :> Component> systemName (comp : 't) =
            match this.TryGetSystem<SystemUncorrelated<'t, 'w>> systemName with
            | Some system -> system.AddComponent comp
            | None -> failwith ("Could not find expected system '" + systemName + "'.")
    
        member this.RemoveComponent<'t when 't : struct and 't :> Component> systemName index =
            match this.TryGetSystem<SystemUncorrelated<'t, 'w>> systemName with
            | Some system -> system.RemoveComponent index
            | None -> failwith ("Could not find expected system '" + systemName + "'.")

/// An Ecs system with components stored by entity id.
type [<AbstractClass>] SystemCorrelated<'t, 'w when 't : struct and 't :> Component> () =
    inherit System<'w> ()

    let mutable components = Array.zeroCreate 32 : 't array
    let mutable freeIndex = 0
    let freeList = Queue<int> ()
    let correlations = dictPlus [] : Dictionary<Guid, int>
    
    member this.Components with get () = components and internal set value = components <- value
    member this.FreeIndex with get () = freeIndex and internal set value = freeIndex <- value
    member internal this.FreeList with get () = freeList
    member internal this.Correlations with get () = correlations

    member this.HasComponent entityId =
        correlations.ContainsKey entityId

    member this.GetComponentIndex entityId =
        let (found, index) = correlations.TryGetValue entityId
        if not found then raise (InvalidOperationException "entityId")
        index

    member this.GetComponent entityId =
        let index = this.GetComponentIndex entityId
        &components.[index]

    member this.GetEntities () =
        correlations.Keys :> _ IEnumerable

    abstract member RegisterEntity : Guid -> 't -> 'w Ecs -> int
    default this.RegisterEntity entityId comp _ =
        match Dictionary.tryGetValue entityId correlations with
        | (false, _) ->
            if freeList.Count = 0 then
                if freeIndex < components.Length - 1 then
                    components.[freeIndex] <- comp
                    freeIndex <- inc freeIndex
                else
                    let arr = Array.zeroCreate (components.Length * 2)
                    components.CopyTo (arr, 0)
                    components <- arr
                    components.[freeIndex] <- comp
                    freeIndex <- inc freeIndex
                let index = dec freeIndex
                correlations.Add (entityId, index)
                index
            else
                let index = freeList.Dequeue ()
                components.[index] <- comp
                index
        | (true, index) ->
            let mutable comp = components.[index]
            comp.RefCount <- inc comp.RefCount
            index

    abstract member UnregisterEntity : Guid -> 'w Ecs -> bool
    default this.UnregisterEntity entityId _ =
        match correlations.TryGetValue entityId with
        | (true, index) ->
            if index <> freeIndex then
                components.[index].RefCount <- dec components.[index].RefCount
                if components.[index].RefCount = 0 then freeList.Enqueue index
            else freeIndex <- dec freeIndex
            true
        | (false, _) -> false

    type 'w Ecs with
    
        member this.QualifyEntity<'t when 't : struct and 't :> Component> systemName entityId =
            let systemOpt = this.TryGetSystem<SystemCorrelated<'t, 'w>> systemName
            if Option.isNone systemOpt then failwith ("Could not find expected system '" + systemName + "'.")
            let system = Option.get systemOpt
            system.HasComponent entityId
    
        member this.IndexEntity<'t when 't : struct and 't :> Component> systemName entityId =
            let systemOpt = this.TryGetSystem<SystemCorrelated<'t, 'w>> systemName
            if Option.isNone systemOpt then failwith ("Could not find expected system '" + systemName + "'.")
            let system = Option.get systemOpt
            &system.GetComponent entityId
    
        member this.RegisterEntity<'t when 't : struct and 't :> Component> systemName entityId comp =
            match this.TryGetSystem<SystemCorrelated<'t, 'w>> systemName with
            | Some system ->
                let _ = system.RegisterEntity entityId comp
                match this.Correlations.TryGetValue entityId with
                | (true, correlation) -> correlation.Add systemName
                | (false, _) -> this.Correlations.Add (entityId, List [systemName])
            | None -> failwith ("Could not find expected system '" + systemName + "'.")
    
        member this.UnregisterEntity<'t when 't : struct and 't :> Component> systemName entityId =
            match this.TryGetSystem<SystemCorrelated<'t, 'w>> systemName with
            | Some system ->
                if system.UnregisterEntity entityId this then
                    match this.Correlations.TryGetValue entityId with
                    | (true, correlation) -> correlation.Add systemName
                    | (false, _) -> this.Correlations.Add (entityId, List [systemName])
            | _ -> failwith ("Could not find expected system '" + systemName + "'.")
    
        member this.CorrelateEntity entityId =
            this.Correlations.[entityId] |>
            Seq.map (fun systemName -> (systemName, this.GetSystem<'w System> systemName)) |>
            dictPlus

        member this.GetEntities<'t when 't : struct and 't :> Component> systemName =
            match this.TryGetSystem<SystemCorrelated<'t, 'w>> systemName with
            | Some system -> system.GetEntities ()
            | _ -> failwith ("Could not find expected system '" + systemName + "'.")

/// An Ecs system that stores multiple components per entity id.
type [<AbstractClass>] SystemMultiplexed<'t, 'w when 't : struct and 't :> Component> () =
    inherit SystemCorrelated<'t ComponentMultiplexed, 'w> ()

    member this.HasMultiplexed multiId entityId =
        if this.HasComponent entityId then
            let comp = this.GetComponent entityId
            comp.Entries.ContainsKey multiId
        else false

    member this.GetMultiplexed multiId entityId =
        let componentMultiplexed = &this.GetComponent entityId
        componentMultiplexed.Entries.[multiId]

    member this.RegisterMultiplexed multiId entityId comp ecs =
        let _ = this.RegisterEntity entityId ecs
        let componentMultiplexed = &this.GetComponent entityId
        do componentMultiplexed.RegisterComponent (multiId, comp)
        multiId

    member this.UnregisterMultiplexed multiId entityId ecs =
        let componentMultiplexed = &this.GetComponent entityId
        let _ = componentMultiplexed.UnregisterComponent multiId
        this.UnregisterEntity entityId ecs

    type 'w Ecs with

        member this.QualifyMultiplexed<'t when 't : struct and 't :> Component> systemName multiId entityId =
            let systemOpt = this.TryGetSystem<SystemMultiplexed<'t, 'w>> systemName
            if Option.isNone systemOpt then failwith ("Could not find expected system '" + systemName + "'.")
            let system = Option.get systemOpt
            system.HasMultiplexed multiId entityId

        member this.IndexMultiplexed<'t when 't : struct and 't :> Component> systemName multiId entityId =
            let systemOpt = this.TryGetSystem<SystemMultiplexed<'t, 'w>> systemName
            if Option.isNone systemOpt then failwith ("Could not find expected system '" + systemName + "'.")
            let system = Option.get systemOpt
            let simplex = system.GetMultiplexed multiId entityId
            &simplex.Simplex

        member this.RegisterMultiplexed<'t when 't : struct and 't :> Component> systemName entityId comp =
            match this.TryGetSystem<SystemMultiplexed<'t, 'w>> systemName with
            | Some system ->
                let _ = system.RegisterMultiplexed entityId comp
                match this.Correlations.TryGetValue entityId with
                | (true, correlation) -> correlation.Add systemName
                | (false, _) -> this.Correlations.Add (entityId, List [systemName])
            | None -> failwith ("Could not find expected system '" + systemName + "'.")

        member this.UnregisterMultiplexed<'t when 't : struct and 't :> Component> systemName multiId entityId =
            match this.TryGetSystem<SystemMultiplexed<'t, 'w>> systemName with
            | Some system ->
                if system.UnregisterMultiplexed multiId entityId this then
                    match this.Correlations.TryGetValue entityId with
                    | (true, correlation) -> correlation.Add systemName
                    | (false, _) -> this.Correlations.Add (entityId, List [systemName])
            | _ -> failwith ("Could not find expected system '" + systemName + "'.")

/// An Ecs system that explicitly associates components by entity id.
type [<AbstractClass>] SystemJunctioned<'t, 'w when 't : struct and 't :> Component> (junctionedSystemNames : string array) =
    inherit SystemCorrelated<'t, 'w> ()

    let mutable junctionsOpt = None : Dictionary<string, 'w System> option

    abstract Junction : Dictionary<string, 'w System> -> Guid -> 't -> 'w Ecs -> 't

    abstract Disjunction : Dictionary<string, 'w System> -> Guid -> 't -> 'w Ecs -> unit

    member this.GetJunctions (ecs : 'w Ecs) =
        match junctionsOpt with
        | Some junctions -> junctions
        | None ->
            let junctions =
                junctionedSystemNames |>
                Array.map (fun sourceName -> (sourceName, ecs.GetSystem<'w System> sourceName)) |>
                dictPlus
            junctionsOpt <- Some junctions
            junctions

    override this.RegisterEntity entityId comp ecs =
        match Dictionary.tryGetValue entityId this.Correlations with
        | (false, _) ->
            let junctions = this.GetJunctions ecs
            let comp = this.Junction junctions entityId comp ecs
            if this.FreeList.Count = 0 then
                if this.FreeIndex < this.Components.Length - 1 then
                    this.Components.[this.FreeIndex] <- comp
                    this.FreeIndex <- inc this.FreeIndex
                else
                    let arr = Array.zeroCreate (this.Components.Length * 2)
                    this.Components.CopyTo (arr, 0)
                    this.Components <- arr
                    this.Components.[this.FreeIndex] <- comp
                    this.FreeIndex <- inc this.FreeIndex
                let index = dec this.FreeIndex
                this.Correlations.Add (entityId, index)
                index
            else
                let index = this.FreeList.Dequeue ()
                this.Components.[index] <- comp
                index
        | (true, index) ->
            let mutable comp = this.Components.[index]
            comp.RefCount <- inc comp.RefCount
            index

    override this.UnregisterEntity entityId ecs =
        match this.Correlations.TryGetValue entityId with
        | (true, index) ->
            let comp = this.Components.[index]
            let junctions = this.GetJunctions ecs
            if index <> this.FreeIndex then
                this.Components.[index].RefCount <- dec this.Components.[index].RefCount
                if this.Components.[index].RefCount = 0 then this.FreeList.Enqueue index
            else this.FreeIndex <- dec this.FreeIndex
            this.Disjunction junctions entityId comp ecs
            true
        | (false, _) -> false

    type 'w Ecs with

        member this.JunctionEntityPlus<'t, 'w when 't : struct and 't :> Component>
            (systemName : string) (junctions : Dictionary<string, 'w System>) (entityId : Guid) (comp : 't) =
            let system = junctions.[systemName] :?> SystemCorrelated<'t, 'w>
            let index = system.RegisterEntity entityId comp this
            ComponentRef.make index system.Components

        member this.JunctionEntity<'t, 'w when 't : struct and 't :> Component>
            (junctions : Dictionary<string, 'w System>) (entityId : Guid) =
            this.JunctionEntityPlus<'t, 'w> typeof<'t>.Name junctions entityId Unchecked.defaultof<'t>

        member this.DisjunctionEntityPlus<'t, 'w when 't : struct and 't :> Component>
            (systemName : string) (junctions : Dictionary<string, 'w System>) (entityId : Guid) =
            let system = junctions.[systemName] :?> SystemCorrelated<'t, 'w>
            system.UnregisterEntity entityId this |> ignore

        member this.DisjunctionEntity<'t, 'w when 't : struct and 't :> Component>
            (junctions : Dictionary<string, 'w System>) (entityId : Guid) =
            this.DisjunctionEntityPlus<'t, 'w> typeof<'t>.Name junctions entityId