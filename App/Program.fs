﻿module Auctions.Program
open Suave
open System

open Auctions.Web
open Auctions.Actors
open Auctions.Environment
open Auctions
open FSharpPlus
open FSharpPlus.Data
open FSharpPlus.Operators
type DictEntry = System.Collections.DictionaryEntry

type CmdArgs =
  { IP : System.Net.IPAddress
    Port : Sockets.Port
    Redis : string option
    Json : string option
    WebHook : Uri option
  }

[<EntryPoint>]
let main argv =
  // parse arguments
  let args =
    let (|Port|_|) : _-> UInt16 option = tryParse
    let (|IPAddress|_|) :_->System.Net.IPAddress option = tryParse
    let (|Uri|_|) (uri:string) :System.Uri option = try System.Uri uri |> Some with | _ -> None
    //default bind to 127.0.0.1:8083
    let defaultArgs =
      { IP = System.Net.IPAddress.Loopback
        Port = 8083us
        Redis = None
        Json = None
        WebHook = None
      }
    let envArgs = Env.vars () |> Env.envArgs "AUCTIONS_"
    let rec parseArgs b args =
      match args with
      | [] -> b
      | "--ip" :: IPAddress ip :: xs -> parseArgs { b with IP = ip } xs
      | "--port" :: Port p :: xs -> parseArgs { b with Port = p } xs
      | "--redis" :: conn :: xs -> parseArgs { b with Redis = Some conn } xs
      | "--json" :: file :: xs -> parseArgs { b with Json = Some file } xs
      | "--web-hook" :: Uri url :: xs -> parseArgs { b with WebHook = Some url } xs
      | invalidArgs ->
        printfn "error: invalid arguments %A" invalidArgs
        printfn "Usage:"
        printfn "    --ip                   ADDRESS     ip address (Default: %O)" defaultArgs.IP
        printfn "    --port                 PORT        port (Default: %i)" defaultArgs.Port
        printfn "    --redis                CONN        redis connection string"
        printfn "    --json                 FILE        path to filename to store commands"
        printfn "    --web-hook             URI         web hook to receive commands and command results"
        exit 1

    argv
    |> List.ofArray
    |> List.append envArgs
    |> parseArgs defaultArgs

  let appenders = seq {
        if Option.isSome args.Redis then yield AppendAndReadBatchRedis(args.Redis.Value) :> IAppendBatch
        if Option.isSome args.Json then yield JsonAppendToFile(args.Json.Value) :> IAppendBatch
      }
  let observers = seq {
    if Option.isSome args.WebHook then yield WebHook.ofUri args.WebHook.Value
  }
  let events = monad.plus {
                  for appender in appenders do
                    yield appender.ReadAll()
                 }
                 |> Async.Parallel |> Async.RunSynchronously
                 |> Seq.collect id
                 |> Seq.toList
  let batchAppend = appenders
                    |> Seq.map (fun a->a.Batch)
                    |> List.ofSeq
  let persist = PersistEvents.create batchAppend
  let observer = Observer.create <| Seq.toList observers
  let time ()= DateTime.UtcNow
  let onIncomingCommand command=
    //persist command
    Domain.Commands [command] |> observer
  let observeCommandResult result =
    match result with Ok r -> persist r | _ -> ()
    Domain.Results [result] |> observer
  // send empty list to observers if any, will cause the program to crash early if observers are misconfigured
  Domain.Commands [] |> observer

  let agent = AuctionDelegator.create(events, onIncomingCommand, time, observeCommandResult)
  // start suave
  startWebServer { defaultConfig with bindings = [ HttpBinding.create HTTP args.IP args.Port ] } (OptionT.run << webPart agent)
  0
