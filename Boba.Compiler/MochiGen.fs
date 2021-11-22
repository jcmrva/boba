﻿namespace Boba.Compiler

module MochiGen =

    open System
    open Boba.Compiler.CoreGen
    open Boba.Core.Common
    open Boba.Core.Syntax
    open Mochi.Core.Instructions
    open Mochi.Core

    /// It is helpful to distinguish between variables that were bound as values vs as functions, because the
    /// latter have a different semantics for the name appearing in the source code (push and call, vs just push).
    /// Continuations have their own special instructions for being called, which is why it is necessary to distinguish
    /// between closures and continuations.
    type EnvEntryKind =
        | EnvValue
        | EnvClosure
        | EnvContinuation

    type EnvEntry = {
        Name: string;
        Kind: EnvEntryKind;
    }

    /// An environment is a stack of 'call frames', each of which is an ordered list of variables
    /// with some information attached to each variable.
    type Env = List<List<EnvEntry>>

    let envContains (env : Env) name = List.exists (List.exists (fun e -> e.Name = name)) env

    let envGet (env : Env) name =
        let (Some frameIndex) = List.tryFindIndex (List.exists (fun e -> e.Name = name)) env
        let (Some entryIndex) = List.tryFindIndex (fun e -> e.Name = name) env.[frameIndex]
        (frameIndex, entryIndex, env.[frameIndex].[entryIndex])

    let closureFrame env free =
        [for v in free do
         let (frameIndex, entryIndex, entry) = envGet env v
         (frameIndex, entryIndex, { Name = v; Kind = entry.Kind })]

    let genInteger size digits =
        match size with
        | Boba.Core.Types.I8 -> II8 (SByte.Parse digits)
        | Boba.Core.Types.U8 -> IU8 (Byte.Parse digits)
        | Boba.Core.Types.I16 -> II16 (Int16.Parse digits)
        | Boba.Core.Types.U16 -> IU16 (UInt16.Parse digits)
        | Boba.Core.Types.I32 -> II32 (Int32.Parse digits)
        | Boba.Core.Types.U32 -> IU32 (UInt32.Parse digits)
        | Boba.Core.Types.I64 -> II64 (Int64.Parse digits)
        | Boba.Core.Types.U64 -> IU64 (UInt64.Parse digits)
        | Boba.Core.Types.ISize -> IISize (int digits)
        | Boba.Core.Types.USize -> IUSize (uint digits)

    let genIntPrimVar isize =
        let sizeSuffix = isize.ToString().ToLower()
        Map.empty
        |> Map.add ("add-" + sizeSuffix) [IIntAdd isize]
        |> Map.add ("addovf-" + sizeSuffix) [IIntAddOvf isize]
        |> Map.add ("sub-" + sizeSuffix) [IIntSub isize]
        |> Map.add ("subovf-" + sizeSuffix) [IIntSubOvf isize]
        |> Map.add ("mul-" + sizeSuffix) [IIntMul isize]
        |> Map.add ("mulovf-" + sizeSuffix) [IIntMulOvf isize]
        |> Map.add ("divremt-" + sizeSuffix) [IIntDivRemT isize]
        |> Map.add ("divremf-" + sizeSuffix) [IIntDivRemF isize]
        |> Map.add ("divreme-" + sizeSuffix) [IIntDivRemE isize]
        |> Map.add ("or-" + sizeSuffix) [IIntOr isize]
        |> Map.add ("and-" + sizeSuffix) [IIntAnd isize]
        |> Map.add ("xor-" + sizeSuffix) [IIntXor isize]
        |> Map.add ("compl-" + sizeSuffix) [IIntComplement isize]
        |> Map.add ("shl-" + sizeSuffix) [IIntShiftLeft isize]
        |> Map.add ("ashr-" + sizeSuffix) [IIntArithShiftRight isize]
        |> Map.add ("lshr-" + sizeSuffix) [IIntLogicShiftRight isize]
        |> Map.add ("eq-" + sizeSuffix) [IIntEqual isize]
        |> Map.add ("lt-" + sizeSuffix) [IIntLessThan isize]
        |> Map.add ("gt-" + sizeSuffix) [IIntGreaterThan isize]
        |> Map.add ("sign-" + sizeSuffix) [IIntSign isize]

    let intPrimMap =
        genIntPrimVar I8
        |> mapUnion fst (genIntPrimVar U8)
        |> mapUnion fst (genIntPrimVar I16)
        |> mapUnion fst (genIntPrimVar U16)
        |> mapUnion fst (genIntPrimVar I32)
        |> mapUnion fst (genIntPrimVar U32)
        |> mapUnion fst (genIntPrimVar I64)
        |> mapUnion fst (genIntPrimVar U64)
        |> mapUnion fst (genIntPrimVar ISize)
        |> mapUnion fst (genIntPrimVar USize)

    let genPrimVar prim =
        match prim with
        | "new-ref" -> [INewRef]
        | "get-ref" -> [IGetRef]
        | "put-ref" -> [IPutRef]

        | "nil-record" -> [IEmptyRecord]

        | "bool-true" -> [ITrue]
        | "bool-false" -> [IFalse]
        | "and-bool" -> [IBoolAnd]
        | "or-bool" -> [IBoolOr]
        | "not-bool" -> [IBoolNot]
        | "xor-bool" -> [IBoolXor]
        | "eq-bool" -> [IBoolEq]

        | "nil-list" -> [IListNil]
        | "cons-list" -> [IListCons]
        | "head-list" -> [IListHead]
        | "tail-list" -> [IListTail]
        | "append-list" -> [IListAppend]

        | _ ->
            if Map.containsKey prim intPrimMap
            then intPrimMap.[prim]
            else failwith $"Primitive '{prim}' not yet implemented."


    let rec genWord program env word =
        match word with
        | WDo -> ([ICallClosure], [])
        | WHandle (ps, h, hs, r) ->
            let (hg, hb) = genExpr program env h
            let handleBody = List.append hg [IComplete]
            
            let hndlThread = [for p in List.rev ps -> { Name = p; Kind = EnvValue }]
            let retFree = Set.difference (exprFree r) (Set.ofList ps)
            let (retG, retBs) = genClosure program env "ret" hndlThread retFree 0 r

            let genOps =
                [for handler in List.rev hs ->
                 let hdlrArgs = [for p in List.rev handler.Params do { Name = p; Kind = EnvValue }]
                 let hdlrApp = { Name = "resume"; Kind = EnvContinuation } :: (List.append hdlrArgs hndlThread)
                 let hdlrClosed = Set.add "resume" (Set.union (Set.ofList handler.Params) (Set.ofList ps))
                 let hdlrFree = Set.difference (exprFree handler.Body) hdlrClosed
                 genClosure program env handler.Name hdlrApp hdlrFree handler.Params.Length handler.Body]

            let opsG = List.collect fst genOps
            let opsBs = List.collect snd genOps

            let afterOffset = handleBody.Length + 1
            let hdlrMeta = program.Handlers.Item hs.Head.Name
            let handle = IHandle (hdlrMeta.HandleId, afterOffset, ps.Length, hs.Length)

            (List.concat [retG; opsG; [handle]; handleBody], List.concat [hb; retBs; opsBs])
        | WIf (b, []) ->
            let (bg, bb) = genExpr program env b
            (List.concat [[IOffsetIfNot bg.Length]; bg], bb)
        | WIf (tc, ec) ->
            let (tcg, tcb) = genExpr program env tc
            let (ecg, ecb) = genExpr program env ec
            (List.concat [[IOffsetIf (ecg.Length + 1)]; ecg; [IOffset tcg.Length]; tcg], List.append tcb ecb)
        | WWhile (c, b) ->
            let (cg, cb) = genExpr program env c
            let (bg, bb) = genExpr program env b
            (List.concat [[IOffset bg.Length]; bg; cg; [IOffsetIf -bg.Length]], List.append cb bb)
        | WLetRecs (rs, b) ->
            let recNames = List.map fst rs
            let frame = List.map (fun v -> { Name = v; Kind = EnvClosure }) recNames
            let (bg, bb) = genExpr program (frame :: env) b

            let recGen = [for r in List.rev rs ->
                          let recFree = Set.difference (exprFree (snd r)) (Set.ofList recNames)
                          genClosure program env (fst r) frame recFree 0 (snd r)]

            let recG = List.collect fst recGen
            let recBs = List.collect snd recGen
            (List.concat [recG; [IMutual recNames.Length; IStore recNames.Length]; bg; [IForget]], List.append bb recBs)
        | WVars (vs, e) ->
            let frame = List.map (fun v -> { Name = v; Kind = EnvValue }) vs
            let (eg, eb) = genExpr program (frame :: env) e
            (List.concat [[IStore (List.length vs)]; eg; [IForget]], eb)

        | WExtension n -> ([IRecordExtend n], [])
        | WRestriction n -> ([IRecordRestrict n], [])
        | WSelect n -> ([IRecordSelect n], [])

        | WFunctionLiteral b ->
            genClosure program env "funLit" [] (exprFree b) 0 b
        | WInteger (i, s) -> ([genInteger s i], [])
        | WCallVar n ->
            if envContains env n
            then
                let (frame, ind, entry) = envGet env n
                match entry.Kind with
                | EnvContinuation -> ([IFind (frame, ind); ICallContinuation], [])
                | EnvClosure -> ([IFind (frame, ind); ICallClosure], [])
                | EnvValue -> failwith $"Bad callvar kind {n}"
            else ([ICall (Label n)], [])
        | WValueVar n ->
            let (frame, ind, entry) = envGet env n
            match entry.Kind with
            | EnvValue -> ([IFind (frame, ind)], [])
            | _ -> failwith $"Bad valvar kind {n}"
        | WOperatorVar n ->
            let hdlr = program.Handlers.Item n
            ([IEscape (hdlr.HandleId, hdlr.HandlerIndex)], [])
        | WConstructorVar n ->
            let ctor = program.Constructors.[n]
            ([IConstruct (ctor.Id, ctor.Args)], [])
        | WTestConstructorVar n ->
            let ctor = program.Constructors.[n]
            ([IIsStruct ctor.Id], [])
        | WPrimVar name -> (genPrimVar name, [])
    and genExpr program env expr =
        let res = List.map (genWord program env) expr
        let wordGen = List.map fst res
        let blockGen = List.map snd res
        (List.concat wordGen, List.concat blockGen)
    and genCallable program env expr =
        let (eg, eb) = genExpr program env expr
        (List.append eg [IReturn], eb)
    and genClosure program env prefix callAppend free args expr =
        let blkId = program.BlockId.Value
        let name = prefix + blkId.ToString()
        program.BlockId.Value <- blkId + 1
        let cf = closureFrame env free
        let closedEntries = List.map (fun (_, _, e) -> e) cf |> List.append callAppend
        let closedFinds = List.map (fun (f, i, _) -> (f, i)) cf
        let (blkGen, blkSub) = genCallable program (closedEntries :: env) expr
        ([IClosure ((Label name), args, closedFinds)], BLabeled (name, blkGen) :: blkSub)
    
    let genBlock program blockName expr =
        let (blockExpr, subBlocks) = genCallable program [] expr
        BLabeled (blockName, blockExpr) :: subBlocks

    let genMain program =
        genBlock program "main" program.Main

    let genProgram program =
        let mainByteCode = genMain program
        let defsByteCodes = Map.toList program.Definitions |> List.collect (uncurry (genBlock program))
        let endByteCode = BLabeled ("end", [INop])
        let entryByteCode = BUnlabeled [ICall (Label "main"); ITailCall (Label "end")]
        List.concat [[entryByteCode]; mainByteCode; defsByteCodes; [endByteCode]]