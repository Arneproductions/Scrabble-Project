﻿namespace DIB

open ScrabbleUtil
open ScrabbleUtil.ServerCommunication

open System.IO

open ScrabbleUtil.DebugPrint

// The RegEx module is only used to parse human input. It is not used for the final product.

module RegEx =
    open System.Text.RegularExpressions

    let (|Regex|_|) pattern input =
        let m = Regex.Match(input, pattern)

        if m.Success then
            Some(List.tail [ for g in m.Groups -> g.Value ])
        else
            None

    let parseMove ts =
        let pattern = @"([-]?[0-9]+[ ])([-]?[0-9]+[ ])([0-9]+)([A-Z]{1})([0-9]+)[ ]?"

        Regex.Matches(ts, pattern)
        |> Seq.cast<Match>
        |> Seq.map (fun t ->
            match t.Value with
            | Regex pattern [ x; y; id; c; p ] -> ((x |> int, y |> int), (id |> uint32, (c |> char, p |> int)))
            | _ -> failwith "Failed (should never happen)")
        |> Seq.toList

module Print =

    let printHand pieces hand =
        hand
        |> MultiSet.fold (fun _ x i -> forcePrint (sprintf "%d -> (%A, %d)\n" x (Map.find x pieces) i)) ()

module State =
    // Make sure to keep your state localised in this module. It makes your life a whole lot easier.
    // Currently, it only keeps track of your hand, your player numer, your board, and your dictionary,
    // but it could, potentially, keep track of other useful
    // information, such as number of players, player turn, etc.

    type state = {
        board         : Parser.board
        dict          : ScrabbleUtil.Dictionary.Dict
        playerId  : uint32
        hand          : MultiSet.MultiSet<uint32>
        players       : array<bool>
        playerTurn    : uint32
    }

    let mkState b d pn h pl pt = {board = b; dict = d;  playerId = pn; hand = h; players = pl; playerTurn = pt}
    let removePlayer st player = st.players[player-1] <- false

    let board st         = st.board
    let dict st          = st.dict
    let playerId st             = st.playerId
    let hand st          = st.hand
    let players st       = st.players
    let playerTurn st    = st.playerTurn

    let removeTileFromHand st tileId = {st with hand = st.hand.Remove tileId }

    let addTileToHand st tileId value = {st with hand = st.hand.Add (tileId, value) }

    (* 
        Updates the board with the function provided. This is created as there are a lot of different
        functions that both parses the board and does other stuff. So this is created as a more general purpose.

        NOTE: We could hide this function and create more specific functions if we want to.
    *)
    let updateBoard f st = { st with board = f st.board }

    let playerIsActive (st:state) player = if st.players[player-1] = true then true else false
    let rec changeTurn (st:state) player = 
        match player >= uint32 st.players.Length with
        | true -> if playerIsActive st 1 then 1u else changeTurn st 1u
        | false -> if playerIsActive st (int player+1) then player+1u else changeTurn st player+1u 

module Scrabble =
    open System.Threading     

    let playGame cstream pieces (st: State.state) =

        let rec aux (st: State.state) =
            Print.printHand pieces (State.hand st)

            // remove the force print when you move on from manual input (or when you have learnt the format)
            forcePrint
                "Input move (format '(<x-coordinate> <y-coordinate> <piece id><character><point-value> )*', note the absence of space between the last inputs)\n\n"

            let input = System.Console.ReadLine()
            let move = RegEx.parseMove input

            debugPrint (sprintf "Player %d -> Server:\n%A\n" (State.playerId st) move) // keep the debug lines. They are useful.
            send cstream (SMPlay move)

            let msg = recv cstream
            debugPrint (sprintf "Player %d <- Server:\n%A\n" (State.playerId st) move) // keep the debug lines. They are useful.

            match msg with
            | RCM (CMPlaySuccess (ms, points, newPieces)) ->
                (* Successful play by you. Update your state (remove old tiles, add the new ones, change turn, etc) *)
                let st' = State.mkState st.board st.dict st.playerId st.hand st.players (State.changeTurn st st.playerId)  // This state needs to be updated
                aux st'
            | RCM (CMPlayed (pid, ms, points)) ->
                (* Successful play by other player. Update your state *)
                let st' = State.mkState st.board st.dict st.playerId st.hand st.players (State.changeTurn st pid) // This state needs to be updated
                aux st'
            | RCM (CMPlayFailed (pid, ms)) ->
                (* Failed play. Update your state *)
                let st' = State.mkState st.board st.dict st.playerId st.hand st.players (State.changeTurn st pid) // This state needs to be updated
                aux st'
            | RCM (CMGameOver _) -> ()
            | RCM a -> failwith (sprintf "not implmented: %A" a)
            | RGPE err ->
                printfn "Gameplay Error:\n%A" err
                aux st


        aux st

    let startGame
        (boardP: boardProg)
        (dictf: bool -> Dictionary.Dict)
        (numPlayers: uint32)
        (playerId: uint32)
        (playerTurn: uint32)
        (hand: (uint32 * uint32) list)
        (tiles: Map<uint32, tile>)
        (timeout: uint32 option)
        (cstream: Stream)
        =
        debugPrint (
            sprintf
                "Starting game!
                      number of players = %d
                      player id = %d
                      player turn = %d
                      hand =  %A
                      timeout = %A\n\n"
                numPlayers
                playerId
                playerTurn
                hand
                timeout
        )

        let dict = dictf true // Uncomment if using a gaddag for your dictionary
        //let dict = dictf false // Uncomment if using a trie for your dictionary
        let board = Parser.mkBoard boardP

        let players = Array.create ((int) numPlayers) true 
                  
        let handSet = List.fold (fun acc (x, k) -> MultiSet.add x k acc) MultiSet.empty hand

        fun () -> playGame cstream tiles (State.mkState board dict  playerId handSet players playerTurn)
        
