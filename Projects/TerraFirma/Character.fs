﻿namespace TerraFirma
open System
open System.Numerics
open Prime
open Nu

type CharacterType =
    | Player
    | Enemy

type JumpState =
    { LastTime : int64
      LastTimeOnGround : int64 }

    static member initial =
        { LastTime = 0L
          LastTimeOnGround = 0L }

type AttackState =
    { AttackTime : int64
      AttackedCharacters : Entity Set
      FollowUpBuffered : bool }

    static member make time =
        { AttackTime = time
          AttackedCharacters = Set.empty
          FollowUpBuffered = false }

type InjuryState =
    { InjuryTime : int64 }

type WoundState =
    { WoundTime : int64 }

type ActionState =
    | NormalState
    | AttackState of AttackState
    | InjuryState of InjuryState
    | WoundState of WoundState

type [<ReferenceEquality; SymbolicExpansion>] Character =
    { CharacterType : CharacterType
      PositionPrevious : Vector3 Queue
      RotationPrevious : Quaternion Queue
      LinearVelocityPrevious : Vector3 Queue
      AngularVelocityPrevious : Vector3 Queue
      HitPoints : int
      ActionState : ActionState
      JumpState : JumpState
      WeaponCollisions : Entity Set
      WalkSpeed : single
      TurnSpeed : single
      JumpSpeed : single
      WeaponModel : StaticModel AssetTag }

    member this.PositionInterp position =
        if not (Queue.isEmpty this.PositionPrevious) then
            let positions = Queue.conj position this.PositionPrevious
            Seq.sum positions / single positions.Length
        else position

    member this.RotationInterp rotation =
        if not (Queue.isEmpty this.RotationPrevious) then
            let rotations = Queue.conj rotation this.RotationPrevious
            if rotations.Length > 1 then
                let unnormalized = Quaternion.Slerp (Seq.head rotations, Seq.last rotations, 0.5f)
                unnormalized.Normalized
            else rotation
        else rotation

    member this.LinearVelocityInterp linearVelocity =
        if not (Queue.isEmpty this.LinearVelocityPrevious) then
            let linearVelocities = Queue.conj linearVelocity this.LinearVelocityPrevious
            Seq.sum linearVelocities / single linearVelocities.Length
        else linearVelocity

    member this.AngularVelocityInterp angularVelocity =
        if not (Queue.isEmpty this.AngularVelocityPrevious) then
            let angularVelocities = Queue.conj angularVelocity this.AngularVelocityPrevious
            Seq.sum angularVelocities / single angularVelocities.Length
        else angularVelocity

    member this.CharacterProperties =
        match this.CharacterType with
        | Player -> CharacterProperties.defaultProperties
        | Enemy -> { CharacterProperties.defaultProperties with PenetrationDepthMax = 0.1f }

    static member private computeTraversalAnimations rotation linearVelocity angularVelocity character =
        match character.ActionState with
        | NormalState ->
            let rotationInterp = character.RotationInterp rotation
            let linearVelocityInterp = character.LinearVelocityInterp linearVelocity
            let angularVelocityInterp = character.AngularVelocityInterp angularVelocity
            let forwardness = (Vector3.Dot (linearVelocityInterp * 32.0f, rotationInterp.Forward))
            let backness = (Vector3.Dot (linearVelocityInterp * 32.0f, -rotationInterp.Forward))
            let rightness = (Vector3.Dot (linearVelocityInterp * 32.0f, rotationInterp.Right))
            let leftness = (Vector3.Dot (linearVelocityInterp * 32.0f, -rotationInterp.Right))
            let turnRightness = (angularVelocityInterp * v3Up).Length () * 48.0f
            let turnLeftness = -turnRightness
            let animations =
                [Animation.make 0L None "Armature|Idle" Loop 1.0f 0.5f None]
            let animations =
                if forwardness >= 0.01f then Animation.make 0L None "Armature|WalkForward" Loop 1.0f forwardness None :: animations
                elif backness >= 0.01f then Animation.make 0L None "Armature|WalkBack" Loop 1.0f backness None :: animations
                else animations
            let animations =
                if rightness >= 0.01f then Animation.make 0L None "Armature|WalkRight" Loop 1.0f rightness None :: animations
                elif leftness >= 0.01f then Animation.make 0L None "Armature|WalkLeft" Loop 1.0f leftness None :: animations
                else animations
            let animations =
                if turnRightness >= 0.01f then Animation.make 0L None "Armature|TurnRight" Loop 1.0f turnRightness None :: animations
                elif turnLeftness >= 0.01f then Animation.make 0L None "Armature|TurnLeft" Loop 1.0f turnLeftness None :: animations
                else animations
            animations
        | _ -> []

    static member private tryComputeActionAnimation time character =
        match character.ActionState with
        | NormalState -> None
        | AttackState attack ->
            let localTime = time - attack.AttackTime
            let soundOpt =
                match localTime with
                | 7L -> Some Assets.Gameplay.SlashSound
                | 67L -> Some Assets.Gameplay.Slash2Sound
                | _ -> None
            let animationStartTime = GameTime.ofUpdates (time - localTime % 55L)
            let animationName = if localTime <= 55 then "Armature|AttackVertical" else "Armature|AttackHorizontal"
            let animation = Animation.once animationStartTime None animationName
            Some (soundOpt, animation, false, false)
        | InjuryState injury ->
            let localTime = time - injury.InjuryTime
            let animationStartTime = GameTime.ofUpdates (time - localTime % 55L)
            let animation = Animation.once animationStartTime None "Armature|WalkBack"
            Some (None, animation, false, false)
        | WoundState wound ->
            let localTime = time - wound.WoundTime
            let animationStartTime = GameTime.ofUpdates (time - localTime % 55L)
            let animation = Animation.loop animationStartTime None "Armature|WalkBack"
            let invisible = localTime / 5L % 2L = 0L
            let destroy = match character.CharacterType with Player -> false | Enemy -> localTime > 60L
            Some (None, animation, invisible, destroy)

    static member private updateInterps position rotation linearVelocity angularVelocity character =

        // update interps
        let character =
            { character with
                PositionPrevious = (if character.PositionPrevious.Length >= Constants.Gameplay.CharacterInterpolationSteps then character.PositionPrevious |> Queue.tail else character.PositionPrevious) |> Queue.conj position
                RotationPrevious = (if character.RotationPrevious.Length >= Constants.Gameplay.CharacterInterpolationSteps then character.RotationPrevious |> Queue.tail else character.RotationPrevious) |> Queue.conj rotation
                LinearVelocityPrevious = (if character.LinearVelocityPrevious.Length >= Constants.Gameplay.CharacterInterpolationSteps then character.LinearVelocityPrevious |> Queue.tail else character.LinearVelocityPrevious) |> Queue.conj linearVelocity
                AngularVelocityPrevious = (if character.AngularVelocityPrevious.Length >= Constants.Gameplay.CharacterInterpolationSteps then character.AngularVelocityPrevious |> Queue.tail else character.AngularVelocityPrevious) |> Queue.conj angularVelocity }

        // ensure previous positions interp aren't stale (such as when an entity is moved in the editor with existing previous position state)
        let character =
            let positionInterp = character.PositionInterp position
            if Vector3.Distance (positionInterp, position) > Constants.Gameplay.CharacterPositionInterpDistanceMax
            then { character with PositionPrevious = List.init Constants.Gameplay.CharacterInterpolationSteps (fun _ -> position) |> Queue.ofList }
            else character

        // fin
        character

    static member private updateMotion isKeyboardKeyDown nav3dFollow time position (rotation : Quaternion) grounded (playerPosition : Vector3) character =

        // update jump state
        let lastTimeOnGround = if grounded then time else character.JumpState.LastTimeOnGround
        let character = { character with Character.JumpState.LastTimeOnGround = lastTimeOnGround }

        // update traversal
        match character.CharacterType with
        | Player ->

            // player traversal
            if character.ActionState = NormalState || not grounded then

                // compute new position
                let forward = rotation.Forward
                let right = rotation.Right
                let walkSpeed = character.WalkSpeed * if grounded then 1.0f else 0.75f
                let walkVelocity =
                    (if isKeyboardKeyDown KeyboardKey.W || isKeyboardKeyDown KeyboardKey.Up then forward * walkSpeed else v3Zero) +
                    (if isKeyboardKeyDown KeyboardKey.S || isKeyboardKeyDown KeyboardKey.Down then -forward * walkSpeed else v3Zero) +
                    (if isKeyboardKeyDown KeyboardKey.A then -right * walkSpeed else v3Zero) +
                    (if isKeyboardKeyDown KeyboardKey.D then right * walkSpeed else v3Zero)
                let position = if walkVelocity <> v3Zero then position + walkVelocity else position

                // compute new rotation
                let turnSpeed = character.TurnSpeed * if grounded then 1.0f else 0.75f
                let turnVelocity =
                    (if isKeyboardKeyDown KeyboardKey.Right then -turnSpeed else 0.0f) +
                    (if isKeyboardKeyDown KeyboardKey.Left then turnSpeed else 0.0f)
                let rotation = if turnVelocity <> 0.0f then rotation * Quaternion.CreateFromAxisAngle (v3Up, turnVelocity) else rotation
                (position, rotation, walkVelocity, v3 0.0f turnVelocity 0.0f, character)

            else (position, rotation, v3Zero, v3Zero, character)

        | Enemy ->
        
            // enemy traversal
            if character.ActionState = NormalState then
                let sphere =
                    if position.Y - playerPosition.Y >= 0.25f
                    then Sphere (playerPosition, 0.1f) // when above player
                    else Sphere (playerPosition, 0.7f) // when at or below player
                let nearest = sphere.Nearest position
                let followOutput = nav3dFollow (Some 1.0f) (Some 10.0f) 0.04f 0.1f position rotation nearest
                (followOutput.NavPosition, followOutput.NavRotation, followOutput.NavLinearVelocity, followOutput.NavAngularVelocity, character)
            else (position, rotation, v3Zero, v3Zero, character)

    static member private updateAction time (position : Vector3) (rotation : Quaternion) (playerPosition : Vector3) character =
        match character.CharacterType with
        | Enemy ->
            match character.ActionState with
            | NormalState ->
                let rotationForwardFlat = rotation.Forward.WithY(0.0f).Normalized
                let positionFlat = position.WithY 0.0f
                let playerPositionFlat = playerPosition.WithY 0.0f
                if position.Y - playerPosition.Y >= 0.25f then // above player
                    if  Vector3.Distance (playerPositionFlat, positionFlat) < 1.0f &&
                        rotationForwardFlat.AngleBetween (playerPositionFlat - positionFlat) < 0.1f then
                        { character with ActionState = AttackState (AttackState.make time) }
                    else character
                elif playerPosition.Y - position.Y < 1.3f then // at or a bit below player
                    if  Vector3.Distance (playerPositionFlat, positionFlat) < 1.75f &&
                        rotationForwardFlat.AngleBetween (playerPositionFlat - positionFlat) < 0.15f then
                        { character with ActionState = AttackState (AttackState.make time) }
                    else character
                else character
            | _ -> character
        | Player -> character

    static member private updateState time character =
        match character.ActionState with
        | NormalState -> character
        | AttackState attack ->
            let actionState =
                let localTime = time - attack.AttackTime
                if localTime < 55 || localTime < 110 && attack.FollowUpBuffered
                then AttackState attack
                else NormalState
            { character with ActionState = actionState }
        | InjuryState injury ->
            let actionState =
                let localTime = time - injury.InjuryTime
                let injuryTime = match character.CharacterType with Player -> 30 | Enemy -> 40
                if localTime < injuryTime
                then InjuryState injury
                else NormalState
            { character with ActionState = actionState }
        | WoundState _ -> character

    static member private computeAnimations time position rotation linearVelocity angularVelocity character =
        ignore<Vector3> position
        let traversalAnimations = Character.computeTraversalAnimations rotation linearVelocity angularVelocity character
        let (soundOpt, animations, invisible, destroy) =
            match Character.tryComputeActionAnimation time character with
            | Some (soundOpt, animation, invisible, destroy) -> (soundOpt, animation :: traversalAnimations, invisible, destroy)
            | None -> (None, traversalAnimations, false, false)
        (soundOpt, animations, invisible, destroy)

    static member private updateAttackedCharacters time character =
        match character.ActionState with
        | AttackState attack ->
            let localTime = time - attack.AttackTime
            let attack =
                match localTime with
                | 55L -> { attack with AttackedCharacters = Set.empty } // reset attack tracking at start of buffered attack
                | _ -> attack
            if localTime >= 20 && localTime < 30 || localTime >= 78 && localTime < 88 then
                let attackingCharacters = Set.difference character.WeaponCollisions attack.AttackedCharacters
                let attack = { attack with AttackedCharacters = Set.union attack.AttackedCharacters character.WeaponCollisions }
                (attackingCharacters, { character with ActionState = AttackState attack })
            else (Set.empty, { character with ActionState = AttackState attack })
        | _ -> (Set.empty, character)

    static member updateInputKey time keyboardKeyData character =
        match character.CharacterType with
        | Player ->

            // jumping
            if keyboardKeyData.KeyboardKey = KeyboardKey.Space && not keyboardKeyData.Repeated then
                let sinceJump = time - character.JumpState.LastTime
                let sinceOnGround = time - character.JumpState.LastTimeOnGround
                if sinceJump >= 12L && sinceOnGround < 10L && character.ActionState = NormalState then
                    let character = { character with Character.JumpState.LastTime = time }
                    (true, character)
                else (false, character)

            // attacking
            elif keyboardKeyData.KeyboardKey = KeyboardKey.Rshift && not keyboardKeyData.Repeated then
                let character =
                    match character.ActionState with
                    | NormalState ->
                        { character with ActionState = AttackState (AttackState.make time) }
                    | AttackState attack ->
                        let localTime = time - attack.AttackTime
                        if localTime > 10L && not attack.FollowUpBuffered
                        then { character with ActionState = AttackState { attack with FollowUpBuffered = true }}
                        else character
                    | InjuryState _ | WoundState _ -> character
                (false, character)
            else (false, character)

        | Enemy -> (false, character)

    static member update isKeyboardKeyDown nav3dFollow time position rotation linearVelocity angularVelocity grounded playerPosition character =
        let character = Character.updateInterps position rotation linearVelocity angularVelocity character
        let (position, rotation, linearVelocity, angularVelocity, character) = Character.updateMotion isKeyboardKeyDown nav3dFollow time position rotation grounded playerPosition character
        let character = Character.updateAction time position rotation playerPosition character
        let character = Character.updateState time character
        let (attackedCharacters, character) = Character.updateAttackedCharacters time character
        let (soundOpt, animations, invisible, destroy) = Character.computeAnimations time position rotation linearVelocity angularVelocity character
        (soundOpt, animations, invisible, destroy, attackedCharacters, position, rotation, character)

    static member initial characterType =
        { CharacterType = characterType
          PositionPrevious = Queue.empty
          RotationPrevious = Queue.empty
          LinearVelocityPrevious = Queue.empty
          AngularVelocityPrevious = Queue.empty
          HitPoints = 5
          ActionState = NormalState
          JumpState = JumpState.initial
          WeaponCollisions = Set.empty
          WalkSpeed = 0.05f
          TurnSpeed = 0.05f
          JumpSpeed = 5.0f
          WeaponModel = Assets.Gameplay.GreatSwordModel }

    static member initialPlayer =
        { Character.initial Player with WalkSpeed = 0.06f }

    static member initialEnemy =
        { Character.initial Enemy with HitPoints = 3 }