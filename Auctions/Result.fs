﻿[<AutoOpen>]
module Auctions.Result

type ResultBuilder() = 
  member this.Bind(x, f) = Result.bind f x
  member this.ReturnFrom x = x
  member this.Zero() = Result.Ok()
  member this.Return(x) = Result.Ok(x)

  member this.Yield x= 
    Ok x
  member this.YieldFrom x= 
    x

  member this.Delay f= f()

let result = new ResultBuilder()

//from https://github.com/SuaveIO/suave/blob/master/src/Suave/WebPart.fs
type AsyncResult<'a,'e> = Async<Result< 'a,'e>>

type AsyncResultBuilder() =
  let bind (f: 'a -> AsyncResult<'b,'e>) (a: AsyncResult<'a,'e>) = async {
    let! p = a
    match p with
    | Error e ->
      return Error e
    | Ok q ->
      let r = f q
      return! r
    }
  let resultAsyncBind (f: 'a -> AsyncResult<'b,'e>) (a: Result<'a,'e>) = async {
    match a with
    | Error e ->
      return Error e
    | Ok q ->
      let r = f q
      return! r
    }
  member this.Return(x:'a) : AsyncResult<'a,'e> = async { return Ok x }
  member this.Zero() : AsyncResult<unit,'e> = async { return Ok () }
  member this.ReturnFrom(x : AsyncResult<'a,'e>) = x
  member this.Delay(f: unit ->  AsyncResult<'a,'e>) = async { return! f () }
  member this.Bind(x :AsyncResult<'a,'e>, f : 'a -> AsyncResult<'b,'e>) : AsyncResult<'b,'e> = bind f x
  member this.Bind(x :Result<'a,'e>, f: 'a -> AsyncResult<'b,'e>): AsyncResult<'b,'e>  = resultAsyncBind f x 


let asyncResult = AsyncResultBuilder()
