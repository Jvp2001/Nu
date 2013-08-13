﻿//module Program = let [<EntryPoint>] main = fun _ -> 0

(*module Program
open System
open System.IO
open System.Text  
open System.Threading
open System.Xml
open System.Xml.Serialization
open System.Runtime.Serialization
open System.Reflection
open Microsoft.FSharp.Reflection

let getUnionTypes<'a> () =
    let nestedTypes = typedefof<'a>.GetNestedTypes (BindingFlags.Public ||| BindingFlags.NonPublic) 
    Array.filter FSharpType.IsUnion nestedTypes

type Alpha =
    { X : int * int
      Y : Alpha option }
      
type [<KnownType "GetTypes">] Beta =
    | A of int * Alpha
    | B of Beta option
    | C of Map<int, Beta>
    static member GetTypes () = getUnionTypes<Beta> ()

let reflectionTest () =
    let alpha = { X = (0, 0); Y = Some { X = (1, 1); Y = None }}
    let betaA = A (0, alpha)
    let betaB = B (Some betaA)
    let betaC = C (Map.singleton 0 betaB)
    let sb = new StringBuilder()
    let xmlSerializer = DataContractSerializer(typeof<Beta>); 
    xmlSerializer.WriteObject(new XmlTextWriter(new StringWriter(sb)), betaC)
    let sr = sb.ToString()
    printfn "%A" sr

let [<EntryPoint>] main _ =
    reflectionTest ()*)
    
module Program
open System
open System.Diagnostics
open System.Threading
open SDL2
open Nu.Run

let [<EntryPoint>] main _ =
    let sdlRendererFlags = enum<SDL.SDL_RendererFlags> (int (uint32 SDL.SDL_RendererFlags.SDL_RENDERER_ACCELERATED ||| uint32 SDL.SDL_RendererFlags.SDL_RENDERER_PRESENTVSYNC))
    let sdlConfig = makeSdlConfig "Nu Game Engine" 100 100 512 512 SDL.SDL_WindowFlags.SDL_WINDOW_SHOWN sdlRendererFlags
    runNu
        (fun event sdlDeps ->
            match event.Value.``type`` with
            | SDL.SDL_EventType.SDL_QUIT -> false
            | _ -> true)
        (fun sdlDeps ->
            true)
        (fun sdlDeps ->
            ignore (SDL.SDL_SetRenderDrawColor (sdlDeps.Renderer, 0uy, 0uy, 127uy, 255uy))
            ignore (SDL.SDL_RenderClear sdlDeps.Renderer)
            true)
        sdlConfig

(*let [<EntryPoint>] main _ =
    withSdlInit
        (fun () -> SDL.SDL_Init SDL.SDL_INIT_EVERYTHING)
        (fun () -> SDL.SDL_Quit ())
        (fun () ->
            withSdlResource
                (fun () -> SDL.SDL_CreateWindow ("Nu Game Engine", 100, 100, 512, 512, SDL.SDL_WindowFlags.SDL_WINDOW_SHOWN))
                (fun window -> SDL.SDL_DestroyWindow window)
                (fun window ->
                    withSdlResource
                        (fun () -> SDL.SDL_CreateRenderer (window, -1, uint32 SDL.SDL_RendererFlags.SDL_RENDERER_ACCELERATED ||| uint32 SDL.SDL_RendererFlags.SDL_RENDERER_PRESENTVSYNC))
                        (fun renderer -> SDL.SDL_DestroyRenderer renderer)
                        (fun renderer ->
                            withSdlResource
                                (fun () -> SDL.SDL_LoadBMP "Image.bmp")
                                (fun bitmap -> SDL.SDL_FreeSurface bitmap)
                                (fun bitmap ->
                                    withSdlResource
                                        (fun () -> SDL.SDL_CreateTextureFromSurface (renderer, bitmap))
                                        resourceNop
                                        (fun texture ->

                                            let mutable sourceRect = SDL.SDL_Rect ()
                                            sourceRect.x <- 0
                                            sourceRect.y <- 0
                                            sourceRect.w <- 512
                                            sourceRect.h <- 512

                                            let mutable destRect = SDL.SDL_Rect ()
                                            destRect.x <- 0
                                            destRect.y <- 0
                                            destRect.w <- 256
                                            destRect.h <- 256
                                             
                                            ignore (SDL.SDL_SetRenderDrawColor (renderer, 125uy, 0uy, 125uy, 255uy))
                                            ignore (SDL.SDL_RenderClear renderer)
                                            ignore (SDL.SDL_SetRenderTarget (renderer, IntPtr.Zero))
                                            ignore (SDL.SDL_RenderCopy (renderer, texture, ref sourceRect, ref destRect))
                                            
                                            SDL.SDL_RenderPresent renderer
                                            Thread.Sleep 3000
                                            0)))))*)