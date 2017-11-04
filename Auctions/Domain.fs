﻿module Auctions.Domain

open System
open System.ComponentModel
open Newtonsoft.Json
open Saithe

type Currency = 
  /// virtual acution currency
  |VAC=1001  
  /// Swedish 'Krona'
  |SEK=752
  /// Danish 'Krone'
  |DKK=208

module Currency=
  let tryParse c : Currency option= Parse.toTryParse Currency.TryParse c

type UserId = string

[<TypeConverter(typeof<ParseTypeConverter<User>>)>]
[<JsonConverter(typeof<ParseTypeJsonConverter<User>>)>]
type User = 
  | BuyerOrSeller of id : UserId * name : string
  | Support of id : UserId
  
  override this.ToString() = 
    match this with
    | BuyerOrSeller(id, name) -> sprintf "BuyerOrSeller|%s|%s" (id) name
    | Support(id) -> sprintf "Support|%s" (id)
  
  static member getId user = 
    match user with
    | BuyerOrSeller(id, _) -> id
    | Support id -> id
  
  static member tryParse user = 
    let userRegex = System.Text.RegularExpressions.Regex("(?<type>\w*)\|(?<id>[^|]*)(\|(?<name>.*))?")
    let m = userRegex.Match(user)
    if m.Success then 
      match (m.Groups.["type"].Value, m.Groups.["id"].Value, m.Groups.["name"].Value) with
      | "BuyerOrSeller", id, name -> Some (BuyerOrSeller(id, name))
      | "Support", id, _ -> Some (Support(id))
      | type', _, _ -> None
    else None
  [<CompiledName("Parse")>]
  static member parse user = 
    match User.tryParse user with
    | Some user->user
    | None -> raise (FormatException "InvalidUser")
    

type BidId = Guid

type AuctionId = int64
let (|Currency|_|) = Currency.tryParse

[<TypeConverter(typeof<ParseTypeConverter<Amount>>)>]
[<JsonConverter(typeof<ParseTypeJsonConverter<Amount>>)>]
type Amount = 
  { value : int64
    currency : Currency }
  override this.ToString() = 
    sprintf "%s%i" (this.currency.ToString()) this.value

  static member tryParse amount = 
    let userRegex = System.Text.RegularExpressions.Regex("(?<currency>[A-Z]+)(?<value>[0-9]+)")
    let m = userRegex.Match(amount)
    if m.Success then 
      match (m.Groups.["currency"].Value, m.Groups.["value"].Value) with
      | Currency c, amount -> Some { currency=c; value=Int64.Parse amount }
      | type', _ -> None
    else None
  [<CompiledName("Parse")>]
  static member parse amount = 
    match Amount.tryParse amount with
    | Some amount->amount
    | None -> raise (FormatException "InvalidAmount")
  static member (+) (a1 : Amount, a2 : Amount) =
      if a1.currency <> a2.currency then failwith "not defined for two different currencies"
      { a1 with value = a1.value + a2.value }
  static member (-) (a1 : Amount, a2 : Amount) = 
      if a1.currency <> a2.currency then failwith "not defined for two different currencies"
      { a1 with value = a1.value - a2.value }
module Amount=
  let zero c= { currency=c ; value=0L}

let (|Amount|_|) = Amount.tryParse

module Timed = 
  let atNow a = (DateTime.UtcNow, a)

module Auctions=
  type EnglishOptions = { 
      /// the seller has set a minimum sale price in advance (the 'reserve' price) 
      /// and the final bid does not reach that price the item remains unsold
      reservePrice: Amount //if the reserve price is 0, that is the equivalent of not setting it 
      /// Sometimes the auctioneer sets a minimum amount by which the next bid must exceed the current highest bid.
      minRaise: Amount //min raise 0 is the equivalent of not setting it
    }

  [<TypeConverter(typeof<ParseTypeConverter<Type>>)>]
  [<JsonConverter(typeof<ParseTypeJsonConverter<Type>>)>]
  type Type=
     /// also known as an open ascending price auction
     /// The auction ends when no participant is willing to bid further
     | English of EnglishOptions
     /// Sealed first-price auction 
     /// In this type of auction all bidders simultaneously submit sealed bids so that no bidder knows the bid of any
     /// other participant. The highest bidder pays the price they submitted.
     /// This type of auction is distinct from the English auction, in that bidders can only submit one bid each.
     | Blind 
     /// Also known as a sealed-bid second-price auction.
     /// This is identical to the sealed first-price auction except that the winning bidder pays the second-highest bid
     /// rather than his or her own
     | Vickrey 
     // | Swedish : same as english, but bidders are not bound by bids and the seller is free to accept or decline any bid 
     // swedish auction might require us to track more things
     // | Dutch of DutchOptions : since this presumes a different kind of model 
     // we could model this as first bid ends auction
     // the time of the bid implies the price
    
    with
    override this.ToString() = 
      match this with
      | English english -> sprintf "English|%s|%s" (english.reservePrice.ToString()) (english.minRaise.ToString())
      | Blind -> sprintf "Blind"
      | Vickrey -> sprintf "Vickrey"
    static member tryParse typ =
      if String.IsNullOrEmpty typ then
        None
      else
        match (typ.Split('|') |> Seq.toList) with
        | "English"::(Amount reservePrice)::(Amount minRaise) :: [] -> 
           Some (Type.English { reservePrice=reservePrice;minRaise=minRaise } )
        | ["Blind"] -> Some (Blind)
        | ["Vickrey"] -> Some (Vickrey)
        | _ -> None
    
    [<CompiledName("Parse")>]
    static member parse typ = 
      match Type.tryParse typ with
      | Some t->t
      | None -> raise (FormatException "Invalid Type")
open Auctions
type Auction = 
  { id : AuctionId
    startsAt : DateTime
    title : string
    endsAt : DateTime
    user : User 
    typ : Type
    currency:Currency
  }

type Bid = 
  { id : BidId
    auction : AuctionId
    user : User
    amount : Amount
    at : DateTime
  }
module Bid=
  let getId (bid : Bid) = bid.id
  let getAmount (bid : Bid) = bid.amount
  let getBidder (bid: Bid) = bid.user

module Auction=
  let getId (auction : Auction) = auction.id
  let hasEnded now (auction : Auction) = auction.endsAt < now
  /// if the bidders are open or anonymous
  /// for instance in a 'swedish' type auction you get to know the other bidders as the winner
  let biddersAreOpen (auction : Auction) = true

  type AuctionEnded = (Amount * User) option

  let getAmountAndWinner (auction : Auction) (bids:Bid list) (now) : AuctionEnded= 
    if hasEnded now auction then
      let bids = bids |> List.sortByDescending Bid.getAmount
      match auction.typ with
      | English english ->
        match bids with
        | [] -> None
        | highestBid :: _ ->
          Some (highestBid.amount, highestBid.user)
      | Vickrey -> 
        match bids with
        | [] -> None
        | highestBid :: secondHighest :: _ ->
          Some (secondHighest.amount, highestBid.user)
        | _ -> 
          None // What happens in a Vickrey auction in this case?
      | Blind ->
        match bids with
        | [] -> None
        | highestBid :: _ ->
          Some (highestBid.amount, highestBid.user)
    else
      None

type Errors = 
  | UnknownAuction of AuctionId
  | UnknownBid of BidId
  | BidAlreadyExists of BidId
  | AuctionAlreadyExists of AuctionId
  | AuctionHasEnded of AuctionId
  | AuctionNotFound of AuctionId
  | SellerCannotPlaceBids of UserId * AuctionId
  | BidCurrencyConversion of BidId * Currency
  | InvalidUserData of string
  | MustPlaceBidOverHighestBid of Amount
  | AlreadyPlacedBid

let containsBidder bidder bids = bids 
                                  |> List.map Bid.getBidder
                                  |> List.contains bidder

let validateBidForAuctionType (auction : Auction) (bids: Bid list) (bid : Bid) = 
  (*
    NOTE: the assumption here is that we do not have
    - English auctions with a large number of bids (i.e. highest bid calculation takes time)
    - Blind or Vickrey with a lot of participants (i.e. the contains bidder operation takes time)
    For real auction engines, you would weigh the pros and cons of these things 
  *)
  match auction.typ with 
  | English english-> 
    match bids with
    | [] -> Ok()
    | xs -> 
      let highestBid = bids |> List.maxBy Bid.getAmount
      // you cannot bid lower than the "current bid"
      if bid.amount > (highestBid.amount + english.minRaise)
      then Ok() 
      else Error (MustPlaceBidOverHighestBid highestBid.amount)

  //| Dutch dutch -> failwith "not implemented"
  | Blind -> 
      if bids|> containsBidder bid.user  then Error AlreadyPlacedBid 
      else Ok()
  | Vickrey -> 
      if bids|> containsBidder bid.user  then Error AlreadyPlacedBid 
      else Ok()

let validateBid (auction : Auction) (bid : Bid) = 
  if bid.at > auction.endsAt then Error(AuctionHasEnded auction.id)
  else if bid.user = auction.user then Error(SellerCannotPlaceBids(User.getId bid.user, auction.id))
  else if bid.amount.currency <> auction.currency then Error(Errors.BidCurrencyConversion(bid.id, bid.amount.currency))
  else Ok()
