﻿module Auctions.Web
open System
open FSharpPlus
open Suave
open Suave.Filters
open Suave.Operators
open Suave.RequestErrors
open Suave.Successful
open Suave.Writers

open Auctions.Domain
open Auctions.Actors
open Auctions
open Newtonsoft.Json
open FSharpPlus.Data
open System.Diagnostics
(* Fake auth in order to simplify web testing *)

type Session = 
  | NoSession
  | UserLoggedOn of User

let authenticated f = 
  context (fun x -> 
    match x.request.header "x-fake-auth" with
    | Choice1Of2 u -> 
        User.tryParse u |> function | Some user->
                                        f (UserLoggedOn(user))
                                    | None ->f NoSession
    | Choice2Of2 _ -> f NoSession)

type WebErrors=
  | DomainError of Errors
  | ParseError of string

module Paths = 
  type Int64Path = PrintfFormat<int64 -> string, unit, string, string, int64>
  
  module Auction = 
    /// /auctions
    let overview = "/auctions"
    /// /auction
    let register = "/auction"
    /// /auction/INT
    let details : Int64Path = "/auction/%d"
    /// /auction/INT/bid
    let placeBid : Int64Path = "/auction/%d/bid"

(* Json API *)

type BidReq = {amount : Amount}
type AddAuctionReq = {
  id : AuctionId
  startsAt : DateTime
  title : string
  endsAt : DateTime
  currency : string
  [<JsonProperty("type")>]
  typ:string
}
type BidJsonResult = { 
  amount:Amount
  bidder:string
}
type AuctionJsonResult = {
  id : AuctionId
  startsAt : DateTime
  title : string
  expiry : DateTime
  currency : Currency
  bids : BidJsonResult array
  winner : string
  winnerPrice : string
}
module JsonResult=
  let exnToInvalidUserData (err:exn)=InvalidUserData (sprintf "%s\n\n%s\n" err.Message err.StackTrace)

  let getAuctionResult (auction,bids,maybeAmountAndWinner) =
    let discloseBidders =Auction.biddersAreOpen auction
    let mapBid (b:Bid) :BidJsonResult = { 
      amount=b.amount
      bidder= if discloseBidders 
              then string b.user 
              else string <| b.user.GetHashCode() // here you might want bidder number
    }

    let (winner,winnerPrice) = 
              match maybeAmountAndWinner with
              | None -> ("","")
              | Some (amount, winner)->
                  (winner.ToString(), amount.ToString())
    { id=auction.id; startsAt=auction.startsAt; title=auction.title;expiry=auction.expiry; currency=auction.currency
      bids=bids |> List.map mapBid |> List.toArray
      winner = winner
      winnerPrice = winnerPrice
    }

  let toPostedAuction user = 
      Json.getBody<AddAuctionReq> 
        >> Result.map (fun a -> 
        let currency= Currency.tryParse a.currency |> Option.defaultValue Currency.VAC
        
        let typ = Type.tryParse a.typ |> Option.defaultValue (TimedAscending { 
                                                                            reservePrice=Amount.zero currency 
                                                                            minRaise =Amount.zero currency
                                                                            timeFrame =TimeSpan.FromSeconds(0.0)
                                                                          })
        { user = user
          id=a.id
          startsAt=a.startsAt
          expiry=a.endsAt
          title=a.title
          currency=currency
          typ=typ
        }
        |> Timed.atNow
        |> AddAuction)
        >> Result.mapError exnToInvalidUserData

  let toPostedPlaceBid id user = 
    Json.getBody<BidReq> 
      >> Result.map (fun a -> 
      let d = DateTime.UtcNow
      (d, 
       { user = user; id= BidId.NewGuid();
         amount=a.amount;auction=id
         at = d })
      |> PlaceBid)
      >> Result.mapError exnToInvalidUserData 

let webPart (agent : AuctionDelegator) = 

  let overview = 
    GET >=> fun (ctx) ->
            async {
              let! r = agent.GetAuctions()
              return! Json.OK (r |>List.toArray) ctx
            }

  let getAuctionResult id : Async<Result<AuctionJsonResult,Errors>>=
      monad {
        let! auctionAndBids = agent.GetAuction id
        match auctionAndBids with
        | Some v-> return Ok <| JsonResult.getAuctionResult v
        | None -> return Error (UnknownAuction id)
      }

  let details id : WebPart= GET >=> (fun ctx->monad{ 
    let! auctionAndBids = agent.GetAuction id
    return!
       match auctionAndBids with
       | Some v-> Json.OK (JsonResult.getAuctionResult v) ctx
       | None -> NOT_FOUND (sprintf "Could not find auction with id %o" id) ctx
  })
  
  /// handle command and add result to repository
  let handleCommandAsync 
    (maybeC:_->Result<Command,_>) :WebPart =
    fun ctx -> monad {
      let userCommand = agent.UserCommand >> map (Result.mapError DomainError)
      match userCommand <!> (maybeC ctx) with
      | Ok asyncR->
          match! asyncR with
          | Ok v->return! Json.OK v ctx
          | Error e-> return! Json.BAD_REQUEST e ctx
      | Error c'->return! Json.BAD_REQUEST c' ctx
    }

  /// register auction
  let register = 
    authenticated (function 
      | NoSession -> UNAUTHORIZED "Not logged in"
      | UserLoggedOn user -> 
        POST >=> handleCommandAsync (JsonResult.toPostedAuction user))

  /// place bid
  let placeBid (id : AuctionId) = 
    authenticated (function 
      | NoSession -> UNAUTHORIZED "Not logged in"
      | UserLoggedOn user -> 
        POST >=> handleCommandAsync (JsonResult.toPostedPlaceBid id user))
  
  choose [ path "/" >=> (OK "")
           path Paths.Auction.overview >=> overview
           path Paths.Auction.register >=> register
           pathScan Paths.Auction.details details
           pathScan Paths.Auction.placeBid placeBid ]

//curl  -X POST -d '{ "id":1,"startsAt":"2018-01-01T10:00:00.000Z","endsAt":"2019-01-01T10:00:00.000Z","title":"First auction", "currency":"VAC" }' -H "x-fake-auth: BuyerOrSeller|a1|Seller"  -H "Content-Type: application/json"  127.0.0.1:8083/auction
//curl  -X POST -d '{ "amount":"VAC10" }' -H "x-fake-auth: BuyerOrSeller|a1|Test"  -H "Content-Type: application/json"  127.0.0.1:8083/auction/1/bid
//curl  -X GET -H "x-fake-auth: BuyerOrSeller|a1|Test"  -H "Content-Type: application/json"  127.0.0.1:8083/auctions 