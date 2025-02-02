namespace Boba.Compiler

module TypeInference =

    open System
    open Boba.Core
    open Boba.Core.Common
    open Boba.Core.Kinds
    open Boba.Core.Types
    open Boba.Core.TypeBuilder
    open Boba.Core.Unification
    open Boba.Core.Fresh
    open Boba.Core.Environment
    open Renamer
    open KindInference

    /// The key inference rule in Boba. Composed words infer their types separately,
    /// then unify the function attributes. The data components unify in a particular
    /// way: the output of the left word unifies with the input of the right word.
    /// This means that the resulting expression has an input of the left word and
    /// and output of the right word, as we would expect from composition.
    /// 
    /// Some of the function attributes are unified, but the Boolean ones are accumulated.
    /// Totality and validity (unsafe code) are accumulated via conjunction, while uniqueness
    /// and clearance (which are always false at the expression level) are accumulated
    /// via disjunction.
    let composeWordTypes leftType rightType =
        let (lContext, lHead) = qualTypeComponents leftType
        let (rContext, rHead) = qualTypeComponents rightType
        let (le, lp, lt, li, lo) = functionValueTypeComponents lHead
        let (re, rp, rt, ri, ro) = functionValueTypeComponents rHead
        let resTy =
            qualType (DotSeq.append lContext rContext)
                (mkFunctionValueType le lp (typeAnd lt rt) li ro
                    (typeOr (valueTypeSharing lHead) (valueTypeSharing rHead)))
        let effConstr = { Left = le; Right = re }
        let permConstr = { Left = lp; Right = rp }
        let insOutsConstr = { Left = lo; Right = ri }

        assert (isTypeWellKinded resTy)
        (resTy, [effConstr; permConstr; insOutsConstr])

    /// Generates a simple polymorphic expression type of the form `(a... -> a...)`
    let freshIdentity (fresh : FreshVars) =
        let io = typeValueSeq (freshSequenceVar fresh)
        let e = freshEffectVar fresh
        let p = freshPermVar fresh
        unqualType (mkExpressionType e p totalAttr io io)

    /// Generates a simple polymorphic expression type of the form `(a... -> b...)`
    /// Useful for recursive function templates. Totality of the generated type is polymorphic.
    let freshTransform (fresh : FreshVars) =
        let i = typeValueSeq (freshSequenceVar fresh)
        let o = typeValueSeq (freshSequenceVar fresh)
        let (e, p, t) = freshFunctionAttributes fresh
        unqualType (mkExpressionType e p t i o)

    /// Generates a simple polymorphic expression type of the form `(a... -> a... ty)` with polymorphic totality.
    let freshPush (fresh : FreshVars) total ty =
        assert (typeKindExn ty = KValue)
        let rest = freshSequenceVar fresh
        let i = typeValueSeq rest
        let o = typeValueSeq (DotSeq.SInd (ty, rest))
        let e = freshEffectVar fresh
        let p = freshPermVar fresh
        unqualType (mkExpressionType e p total i o)
    
    /// Generates a simple expression type of the form `(a... b --> a... c)`, guaranteed to total and valid.
    let freshModifyTop (fresh : FreshVars) valIn valOut =
        assert (typeKindExn valIn = KValue)
        assert (typeKindExn valOut = KValue)
        let rest = freshSequenceVar fresh
        let i = typeValueSeq (DotSeq.ind valIn rest)
        let o = typeValueSeq (DotSeq.ind valOut rest)
        let e = freshEffectVar fresh
        let p = freshPermVar fresh
        unqualType (mkExpressionType e p totalAttr i o)

    /// Generates a simple polymorphic expression type of the form `(a... -> a... ty)` assumed to be total.
    let freshPushWord (fresh : FreshVars) ty word = (freshPush fresh totalAttr ty, [], [word])
    
    let freshPopped (fresh: FreshVars) tys =
        let rest = freshSequenceVar fresh
        let o = typeValueSeq rest
        let i = typeValueSeq (DotSeq.append (DotSeq.ofList (List.rev tys)) rest)
        let e = freshEffectVar fresh
        let p = freshPermVar fresh
        unqualType (mkExpressionType e p totalAttr i o)

    /// Generate a qualified type of the form `(a... ty1 ty2 ... --> b...)`.
    let freshResume (fresh: FreshVars) tys outs =
        let i = typeValueSeq (DotSeq.append (DotSeq.ofList (List.rev tys)) (freshSequenceVar fresh))
        let (e, p, t) = freshFunctionAttributes fresh
        unqualType (mkExpressionType e p t i outs)
    
    /// The sharing attribute on a closure is the disjunction of all of the free variables referenced
    /// by the closure, forcing it to be unique if any of the free variables it references are also unique.
    let sharedClosure env expr =
        List.ofSeq (Boba.Compiler.Syntax.exprFree expr)
        |> List.map (lookup env >> Option.map (fun entry -> schemeSharing entry.Type))
        |> List.collect Option.toList
        |> attrsToDisjunction KSharing

    let getWordEntry env name =
        match lookup env name with
        | Some entry -> entry
        | None -> failwith $"Could not find {name} in the environment"

    let getPatternEntry env name =
        match lookupPattern env name with
        | Some entry -> entry
        | None -> failwith $"Could not find {name} in the environment"

    /// Here, when we instantiate a type from the type environment, we also do inline dictionary
    /// passing, putting in placeholders for the dictionaries that will get resolved to either a
    /// dictionary parameter or dictionary value during generalization.
    /// More details on this implementation tactic: "Implementing Type Classes",
    /// https://citeseerx.ist.psu.edu/viewdoc/download?doi=10.1.1.53.3952&rep=rep1&type=pdf
    let instantiateAndAddPlaceholders fresh env name word =
        let entry = getWordEntry env name
        let instantiated = instantiateExn fresh entry.Type
        // variable types distinguished from 'word' (function) types
        let instantiated =
            if entry.IsVariable
            then freshPush fresh totalAttr instantiated
            else instantiated
        let replaced = 
            if entry.IsVariable
            then
                [word]
            elif entry.IsOverload
            then
                let methodPredicate =
                    DotSeq.at 0 (qualTypeContext instantiated)
                    |> Option.defaultWith (fun () -> failwith $"Overloaded types must have at least one qualifier, got {instantiated}")
                [Syntax.EMethodPlaceholder (name, methodPredicate)]
            elif entry.IsRecursive
            then [Syntax.ERecursivePlaceholder (name, qualTypeHead instantiated)]
            else List.append (DotSeq.map Syntax.EOverloadPlaceholder (qualTypeContext instantiated) |> DotSeq.toList) [word]
        printfn $"Inferred {instantiated} for {name}"
        (instantiated, [], replaced)
    
    /// Compute a set of forced sharing constraints based on whether a variable is used
    /// more than once in an expression. Simplistic and forces some work on the programmer
    /// but highly effective.
    let sharingAnalysis fresh varTypes exprs =
        let vars = List.map fst varTypes |> Set.ofList
        let sharedVars =
            Seq.map Syntax.exprMaxOccurences exprs
            |> Seq.fold Syntax.chooseOccurenceMap Map.empty
            |> Map.filter (fun k _ -> Set.contains k vars)
            |> Map.filter (fun _ v -> v > 1)
            |> Map.fold (fun s k _ -> Set.add k s) Set.empty
        let sharedTys = List.filter (fun vt -> Set.contains (fst vt) sharedVars) varTypes |> List.map snd
        [for ty in sharedTys do { Left = ty; Right = mkValueType (freshDataVar fresh) sharedAttr }]

    /// Given two function types which represent two 'branches' of an expression
    /// e.g. in an if-then-else, generates constraints for only the parts that
    /// should be unified, and returns a type composing the parts that should be
    /// composed (i.e. totality, sharing)
    let unifyBranches br1 br2 =
        let (br1Context, br1Head) = qualTypeComponents br1
        let (br2Context, br2Head) = qualTypeComponents br2
        let (br1e, br1p, br1t, br1i, br1o) = functionValueTypeComponents br1Head
        let (br2e, br2p, br2t, br2i, br2o) = functionValueTypeComponents br2Head
        let effConstr = { Left = br1e; Right = br2e }
        let permConstr = { Left = br1p; Right = br2p }
        let insConstr = { Left = br1i; Right = br2i }
        let outsConstr = { Left = br1o; Right = br2o }
        let totalComp = typeAnd br1t br2t
        let shareComp = typeOr (valueTypeSharing br1Head) (valueTypeSharing br2Head)
        let unifiedType = qualType (DotSeq.append br1Context br2Context) (mkFunctionValueType br1e br1p totalComp br1i br1o shareComp)
        unifiedType, [effConstr; permConstr; insConstr; outsConstr]

    let tuplizeDotPatVar fresh vars =
        match vars with
        | [] -> []
        | [(n, ty)] -> [(n, mkTupleType (DotSeq.dot ty DotSeq.SEnd) (freshShareVar fresh))]
        | _ -> failwith $"Only single variables are allowed in dotted patterns currently, got {vars}."

    let rec inferPattern fresh env pattern =
        match pattern with
        | Syntax.PTuple ps ->
            let inferred = DotSeq.map (inferPattern fresh env) ps
            let infPs = DotSeq.map (fun (_, _, t) -> t) inferred
            // make sure to turn splat vars into tuples for when we use them
            // TODO: this doesn't allow for programmer defined gather/spread; is this a problem?
            let vs =
                DotSeq.mapDotted (fun hasDot (v, _, _) -> if hasDot then tuplizeDotPatVar fresh v else v) inferred
                |> DotSeq.toList
                |> List.concat
            let cs = DotSeq.map (fun (_, c, _) -> c) inferred |> DotSeq.toList |> List.concat

            // build a tuple type that enforces sharing attributes
            let datas = DotSeq.map (fun _ -> freshDataVar fresh) infPs
            let shares = DotSeq.map (fun _ -> freshShareVar fresh) infPs
            let vals = DotSeq.zipWith datas shares mkValueType
            let shareOuters = DotSeq.mapDotted (fun hasDot v -> if hasDot then typeVarToDotVar v else v) shares |> DotSeq.toList
            let ctorShare = (freshShareVar fresh) :: shareOuters |> attrsToDisjunction KSharing
            let infTy = mkTupleType vals ctorShare

            let constrs = zipWith (fun (inf, templ) -> { Left = inf; Right = templ }) (DotSeq.toList infPs) (DotSeq.toList vals)
            vs, List.append constrs cs, infTy
        | Syntax.PList _ -> failwith "Inference for list patterns not yet implemented."
        | Syntax.PVector _ -> failwith "Inference for vector patterns not yet implemented."
        | Syntax.PSlice _ -> failwith "Inference for slice patterns not yet implemented."
        | Syntax.PRecord _ -> failwith "Inference for record patterns not yet implemented."
        | Syntax.PConstructor (name, ps) ->
            let vs, cs, infPs = DotSeq.map (inferPattern fresh env) ps |> DotSeq.toList |> List.unzip3
            let (TSeq (templateTy, KValue)) = instantiateExn fresh (getPatternEntry env name.Name.Name)

            // build a constructor type that enforces sharing attributes
            let all = DotSeq.toList templateTy
            let args = List.take (List.length all - 1) all
            let ctorShare = (freshShareVar fresh) :: List.map valueTypeSharing args |> attrsToDisjunction KSharing
            let ctorTy = mkValueType (List.last all) ctorShare

            let constrs = List.zip infPs args |> List.map (fun (inf, template) -> { Left = inf; Right = template })
            List.concat vs, List.append constrs (List.concat cs), ctorTy
        | Syntax.PNamed (n, p) ->
            let (vs, cs, ty) = inferPattern fresh env p
            (Syntax.nameToString n, ty) :: vs, cs, ty
        | Syntax.PRef r ->
            let vs, cs, ty = inferPattern fresh env r
            let expanded = freshValueComponentType fresh
            let heap = freshHeapVar fresh
            let ref =
                mkRefValueType heap expanded
                    (typeOr (freshShareVar fresh) (valueTypeSharing expanded))
            vs, { Left = ty; Right = expanded } :: cs, ref
        | Syntax.PWildcard -> ([], [], freshValueVar fresh)
        | Syntax.PInteger i -> ([], [], freshIntValueType fresh i.Size)
        | Syntax.PDecimal d -> ([], [], freshFloatValueType fresh d.Size)
        | Syntax.PString _ -> ([], [], freshStringValueType fresh (freshTrustVar fresh) (freshClearVar fresh))
        | Syntax.PTrue _ -> ([], [], freshBoolValueType fresh)
        | Syntax.PFalse _ -> ([], [], freshBoolValueType fresh)
    
    let extendPushVars env varTypes =
        List.fold (fun env vt -> extendVar env (fst vt) (schemeType [] (snd vt))) env varTypes

    let rec inferExpr (fresh : FreshVars) env expr =
        let exprInferred = List.map (inferWord fresh env) expr
        let composed =
            List.fold
                (fun (ty, constrs, expanded) (next, newConstrs, nextExpand) ->
                    let (uniTy, uniConstrs) = composeWordTypes ty next
                    (uniTy, append3 constrs newConstrs uniConstrs, List.append expanded nextExpand))
                (freshIdentity fresh, [], [])
                exprInferred
        composed
    and inferWord fresh env word =
        match word with
        | Syntax.EStatementBlock ss ->
            let (ssTy, ssCnstrs, ssPlc) = inferBlock fresh env ss
            (ssTy, ssCnstrs, [Syntax.EStatementBlock ssPlc])
        | Syntax.EHandle (ps, hdld, hdlrs, aft) ->
            inferHandle fresh env ps hdld hdlrs aft
        | Syntax.EInject _ -> failwith $"Inference not yet implemented for inject expressions."
        | Syntax.EMatch (cs, o) -> inferMatch fresh env cs o
        | Syntax.EIf (cond, thenClause, elseClause) ->
            // infer types for the sub expressions separately
            let (infCond, constrsCond, condExpand) = inferExpr fresh env cond
            let (infThen, constrsThen, thenExpand) = inferBlock fresh env thenClause
            let (infElse, constrsElse, elseExpand) = inferBlock fresh env elseClause
            let condTemplate = freshPopped fresh [freshBoolValueType fresh]
            // make sure the 'then' and 'else' branches unify to the same type
            let infThenElse, thenElseConstrs = unifyBranches infThen infElse
            let (condJoin, constrsCondJoin) = composeWordTypes infCond condTemplate
            let (infJoin, constrsJoin) = composeWordTypes condJoin infThenElse

            let constrs = List.concat [constrsCond; constrsThen; constrsElse; constrsJoin; constrsCondJoin; thenElseConstrs]
            infJoin, constrs, [Syntax.EIf (condExpand, thenExpand, elseExpand)]
        | Syntax.EWhile (cond, body) ->
            let (infCond, constrsCond, condExpand) = inferExpr fresh env cond
            let (infBody, constrsBody, bodyExpand) = inferBlock fresh env body
            let condTemplate = freshPopped fresh [freshBoolValueType fresh]
            let condJoin, constrsCondJoin = composeWordTypes infCond condTemplate
            let bodyJoin, constrsBodyJoin = composeWordTypes condJoin infBody
            let whileJoin, constrsWhileJoin = composeWordTypes bodyJoin condJoin
            let constrs = List.concat [constrsCond; constrsBody; constrsCondJoin; constrsBodyJoin; constrsWhileJoin]
            whileJoin, constrs, [Syntax.EWhile (condExpand, bodyExpand)]
        | Syntax.EFunctionLiteral exp ->
            let eTy, eCnstrs, ePlc = inferExpr fresh env exp
            let eTyContext, eTyHead = qualTypeComponents eTy
            let ne = freshEffectVar fresh
            let np = freshPermVar fresh
            let asValue = mkValueType (valueTypeData eTyHead) (sharedClosure env exp)
            let rest = freshSequenceVar fresh
            let i = typeValueSeq rest
            let o = typeValueSeq (DotSeq.ind asValue rest)
            (qualType eTyContext (mkExpressionType ne np totalAttr i o), eCnstrs, [Syntax.EFunctionLiteral ePlc])
        | Syntax.ETupleLiteral [] ->
            // tuple literals with no splat expression just create an empty tuple
            let ne = freshEffectVar fresh
            let np = freshPermVar fresh
            let rest = freshSequenceVar fresh
            let i = typeValueSeq rest
            let o = typeValueSeq (DotSeq.ind (mkTupleType DotSeq.SEnd (freshShareVar fresh)) rest)
            (unqualType (mkExpressionType ne np totalAttr i o), [], [Syntax.ETupleLiteral []])
        | Syntax.ETupleLiteral exp ->
            // TODO: this doesn't seem like it will handle gather/spread semantics for stack splatting
            // the splat expression in a tuple literal must put a record on top of the stack
            let (infExp, constrsExp, expExpand) = inferExpr fresh env exp
            let ne = freshEffectVar fresh
            let np = freshPermVar fresh
            let ns = freshValueVar fresh
            let tupVal = mkTupleType (DotSeq.dot ns DotSeq.SEnd) (freshShareVar fresh)
            let rest = freshSequenceVar fresh
            let io = typeValueSeq (DotSeq.ind tupVal rest)
            let verifyRecTop = unqualType (mkExpressionType ne np totalAttr io io)
            let (infVer, constrsVer) = composeWordTypes infExp verifyRecTop
            infVer, List.append constrsExp constrsVer, [Syntax.ETupleLiteral expExpand]
        | Syntax.ERecordLiteral [] ->
            // record literals with no splat expression just create an empty record
            let ne = freshEffectVar fresh
            let np = freshPermVar fresh
            let rest = freshSequenceVar fresh
            let i = typeValueSeq rest
            let o = typeValueSeq (DotSeq.ind (mkRecordValueType (TEmptyRow KField) (freshShareVar fresh)) rest)
            (unqualType (mkExpressionType ne np totalAttr i o), [], [Syntax.ERecordLiteral []])
        | Syntax.ERecordLiteral exp ->
            // the splat expression in a record literal must put a record on top of the stack
            let (infExp, constrsExp, expExpand) = inferExpr fresh env exp
            let ne = freshEffectVar fresh
            let np = freshPermVar fresh
            let nr = freshFieldVar fresh
            let recVal = mkRecordValueType nr (freshShareVar fresh)
            let rest = freshSequenceVar fresh
            let io = typeValueSeq (DotSeq.ind recVal rest)
            let verifyRecTop = unqualType (mkExpressionType ne np totalAttr io io)
            let (infVer, constrsVer) = composeWordTypes infExp verifyRecTop
            infVer, List.append constrsExp constrsVer, [Syntax.ERecordLiteral expExpand]
        | Syntax.EExtension name ->
            let ne = freshEffectVar fresh
            let np = freshPermVar fresh
            let fieldVal = mkValueType (freshDataVar fresh) (freshShareVar fresh)
            let nr = freshFieldVar fresh
            let recRow = mkFieldRowExtend name.Name fieldVal nr
            let ns = freshShareVar fresh
            let recVal = mkRecordValueType recRow ns
            let rest = freshSequenceVar fresh
            let i = typeValueSeq (DotSeq.ind fieldVal (DotSeq.ind (mkRecordValueType nr ns) rest))
            let o = typeValueSeq (DotSeq.ind recVal rest)
            (unqualType (mkExpressionType ne np totalAttr i o), [], [Syntax.EExtension name])
        | Syntax.ESelect (false, name) ->
            // false = don't leave the record on the stack, just pop it. Makes selecting shared data
            // out of a unique record multiple times difficult, but probably easiest to use otherwise.
            let ne = freshEffectVar fresh
            let np = freshPermVar fresh
            let rs = freshShareVar fresh
            let fieldVal = mkValueType (freshDataVar fresh) rs
            let nr = freshFieldVar fresh
            let ns = freshShareVar fresh
            let rest = freshSequenceVar fresh
            let i = typeValueSeq (DotSeq.ind (mkRecordValueType (mkFieldRowExtend name.Name fieldVal nr) (typeOr rs ns)) rest)
            let o = typeValueSeq (DotSeq.ind fieldVal rest)
            (unqualType (mkExpressionType ne np totalAttr i o), [], [Syntax.ESelect (false, name)])
        | Syntax.ESelect (true, name) ->
            // true = leave the record on the stack while selecting shared data out of it. Makes it easier
            // to select shared data out of a unique record multiple times, but otherwise not useful.
            let ne = freshEffectVar fresh
            let np = freshPermVar fresh
            let fieldVal = mkValueType (freshDataVar fresh) sharedAttr
            let nr = freshFieldVar fresh
            let recVal = mkRecordValueType (mkFieldRowExtend name.Name fieldVal nr) (freshShareVar fresh)
            let rest = freshSequenceVar fresh
            let i = typeValueSeq (DotSeq.ind recVal rest)
            let o = typeValueSeq (DotSeq.ind fieldVal (DotSeq.ind recVal rest))
            (unqualType (mkExpressionType ne np totalAttr i o), [], [Syntax.ESelect (true, name)])
        | Syntax.EVariantLiteral (name, exp) ->
            let (infExp, constrsExp, expExpand) = inferExpr fresh env exp
            let ne = freshEffectVar fresh
            let np = freshPermVar fresh
            let fieldVal = freshValueVar fresh
            let nr = freshFieldVar fresh
            let rest = freshSequenceVar fresh
            let i = typeValueSeq (DotSeq.ind fieldVal rest)
            let o = typeValueSeq (DotSeq.ind (mkVariantValueType (mkFieldRowExtend name.Name fieldVal nr) (freshShareVar fresh)) rest)
            let varLit = unqualType (mkExpressionType ne np totalAttr i o)
            let varInf, varConstrs = composeWordTypes infExp varLit
            varInf, List.append constrsExp varConstrs, [Syntax.EVariantLiteral (name, expExpand)]
        | Syntax.ECase (cs, other) ->
            let unifyBranchFold (ty, constrs) next =
                let ret, moreConstrs = unifyBranches ty next
                ret, List.append moreConstrs constrs
            let cShare = freshShareVar fresh
            let (infCs, constrsCs, csExpand) = List.map (inferCaseClause fresh env cShare) cs |> List.unzip3
            let (infOther, constrsOther, otherExp) = inferExpr fresh env other
            let infJoin, clauseJoins = List.fold unifyBranchFold (infOther, []) infCs
            infJoin, List.concat [List.concat constrsCs; clauseJoins; constrsOther], [Syntax.ECase (csExpand, otherExp)]

        | Syntax.ETrust ->
            let valClear = freshClearVar fresh
            let valShare = freshShareVar fresh
            let dataCtor = freshTypeVar fresh (KArrow (KTrust, KArrow (KClearance, KData)))
            let valIn = mkValueType (typeApp (typeApp dataCtor (freshTrustVar fresh)) valClear) valShare
            let valOut = mkValueType (typeApp (typeApp dataCtor trustedAttr) valClear) valShare
            freshModifyTop fresh valIn valOut, [], [Syntax.ETrust]
        | Syntax.EDistrust ->
            let valClear = freshClearVar fresh
            let valShare = freshShareVar fresh
            let dataCtor = freshTypeVar fresh (KArrow (KTrust, KArrow (KClearance, KData)))
            let valIn = mkValueType (typeApp (typeApp dataCtor (freshTrustVar fresh)) valClear) valShare
            let valOut = mkValueType (typeApp (typeApp dataCtor untrustedAttr) valClear) valShare
            freshModifyTop fresh valIn valOut, [], [Syntax.ETrust]
        | Syntax.EAudit -> failwith "Audit type inference not yet implemented"
            // let valData = freshDataVar fresh
            // let valClear = freshClearVar fresh
            // let valShare = freshShareVar fresh
            // let valIn = mkValueType valData trustedAttr valClear valShare
            // let valOut = mkValueType valData (freshTrustVar fresh) valClear valShare
            // freshModifyTop fresh valIn valOut, [], [Syntax.EAudit]

        | Syntax.EWithState e ->
            // need to do some 'lightweight' generalization here to remove the heap type
            // we have to verify that it is not free in the environment so that we can
            // soundly remove it from the list of effects in the inferred expressions
            let inferred, constrs, expanded = inferBlock fresh env e
            let subst = solveAll fresh constrs
            let solvedContext, solvedHead = typeSubstExn subst inferred |> qualTypeComponents

            // we filter out the first state eff, since it is the most deeply nested if there are multiple
            let effType, pt, tt, it, ot = functionValueTypeComponents solvedHead
            let effRow = typeToRow effType
            let leftOfEff = List.takeWhile (fun e -> rowElementHead e <> (TPrim PrState)) effRow.Elements
            let eff = List.skipWhile (fun e -> rowElementHead e <> (TPrim PrState)) effRow.Elements |> List.head
            let rightOfEff = List.skipWhile (fun e -> rowElementHead e <> (TPrim PrState)) effRow.Elements |> List.skip 1

            // TODO: apply substitution to environment and check for free variable here

            let newRow = List.append leftOfEff rightOfEff
            let newType =
                qualType solvedContext
                    (mkFunctionValueType
                        (rowToType { Elements = newRow; RowEnd = effRow.RowEnd; ElementKind = effRow.ElementKind })
                        pt
                        tt
                        it
                        ot
                        (valueTypeSharing solvedHead))
            (newType, constrs, [Syntax.EWithState expanded])
        | Syntax.ENewRef ->
            // newref : a... b^u --st[h]-> a... ref<h,b^u>^v
            let rest = freshSequenceVar fresh
            let e = freshEffectVar fresh
            let p = freshPermVar fresh
            let heap = freshHeapVar fresh
            let value = freshValueComponentType fresh
            let ref = mkRefValueType heap value (freshShareVar fresh)
            let st = typeApp (TPrim PrState) heap
            let i = typeValueSeq (DotSeq.ind value rest)
            let o = typeValueSeq (DotSeq.ind ref rest)
            let expr = mkExpressionType (mkRowExtend st e) p totalAttr i o
            (unqualType expr, [], [Syntax.ENewRef])
        | Syntax.EGetRef ->
            // getref : a... ref<h,b^u>^u|v --st[h]-> a... b^u
            let rest = freshSequenceVar fresh
            let e = freshEffectVar fresh
            let p = freshPermVar fresh
            let heap = freshHeapVar fresh
            let value = freshValueComponentType fresh
            let ref =
                mkRefValueType heap value
                    (typeOr (freshShareVar fresh) (valueTypeSharing value))
            let st = typeApp (TPrim PrState) heap
            let i = typeValueSeq (DotSeq.ind ref rest)
            let o = typeValueSeq (DotSeq.ind value rest)
            let expr = mkExpressionType (mkRowExtend st e) p totalAttr i o
            (unqualType expr, [], [Syntax.EGetRef])
        | Syntax.EPutRef ->
            // putref : a... ref<h,b^u>^v b^w --st[h]-> a... ref<h,b^w>^v
            let rest = freshSequenceVar fresh
            let e = freshEffectVar fresh
            let p = freshPermVar fresh
            let heap = freshHeapVar fresh
            let oldValue = freshValueComponentType fresh
            let newValue = freshValueComponentType fresh
            let oldRef = mkRefValueType heap oldValue (freshShareVar fresh)
            let newRef = mkRefValueType heap newValue (freshShareVar fresh)
            let st = typeApp (TPrim PrState) heap
            let i = typeValueSeq (DotSeq.ind newValue (DotSeq.ind oldRef rest))
            let o = typeValueSeq (DotSeq.ind newRef rest)
            let expr = mkExpressionType (mkRowExtend st e) p totalAttr i o
            (unqualType expr, [], [Syntax.EPutRef])

        | Syntax.EUntag ->
            let valData = freshTypeVar fresh (karrow KUnit KData)
            let valUnit = freshUnitVar fresh
            let s = freshShareVar fresh
            let tagged = mkValueType (typeApp valData valUnit) s
            let untagged = mkValueType (typeApp valData (TAbelianOne KUnit)) s
            freshModifyTop fresh tagged untagged, [], [Syntax.EUntag]
        | Syntax.EBy n ->
            let byUnit = (getWordEntry env n.Name.Name).Type.Body
            let valData = freshTypeVar fresh (karrow KUnit KData)
            let valUnit = freshUnitVar fresh
            let s = freshShareVar fresh
            let untagged = mkValueType (typeApp valData valUnit) s
            let tagged = mkValueType (typeApp valData (typeMul valUnit byUnit)) s
            freshModifyTop fresh untagged tagged, [], [Syntax.EBy n]
        | Syntax.EPer n ->
            let byUnit = (getWordEntry env n.Name.Name).Type.Body
            let valData = freshTypeVar fresh (karrow KUnit KData)
            let valUnit = freshUnitVar fresh
            let s = freshShareVar fresh
            let untagged = mkValueType (typeApp valData valUnit) s
            let tagged = mkValueType (typeApp valData (typeMul valUnit (typeExp byUnit -1))) s
            freshModifyTop fresh untagged tagged, [], [Syntax.EPer n]

        | Syntax.EDo ->
            let irest = freshSequenceVar fresh
            let i = typeValueSeq irest
            let o = typeValueSeq (freshSequenceVar fresh)
            let s = freshShareVar fresh
            let (e, p, t) = freshFunctionAttributes fresh
            let called = mkFunctionValueType e p t i o s
            let caller = mkFunctionValueType e p t (typeValueSeq (DotSeq.ind called irest)) o sharedAttr
            (unqualType caller, [], [Syntax.EDo])
        | Syntax.EIdentifier id ->
            instantiateAndAddPlaceholders fresh env id.Name.Name word
        | Syntax.EDecimal d ->
            freshPushWord fresh (freshFloatValueType fresh d.Size) word
        | Syntax.EInteger i ->
            freshPushWord fresh (freshIntValueType fresh i.Size) word
        | Syntax.EString _ ->
            freshPushWord fresh (freshStringValueType fresh trustedAttr clearAttr) word
        | Syntax.ETrue ->
            freshPushWord fresh (freshBoolValueType fresh) word
        | Syntax.EFalse ->
            freshPushWord fresh (freshBoolValueType fresh) word

        | _ -> failwith $"Inference not yet implemented for {word}"

    /// Match expressions have an optional `otherwise` expression that behaves as a catch-all.
    /// If this expression is not present, we must evaluate the pattern matches to determine whether
    /// we should inject a 'may fail pattern match' exception into the effect row type. If the expression
    /// is present, we definitely don't have to, because if all matches fail the `otherwise` expression
    /// will be evaluated on the inputs. Since we unify all branches of a match expression together, all
    /// branches will take the same input types and return the same output types. Totality of the expression
    /// is thus the conjunction of all its branches (if one is not total, the whole thing is not total),
    /// and if one of the branches calls unsafe code, the whole expression must be deemed unsafe.
    /// Most components of the expression type can be directly unified, but the Boolean attributes must
    /// be accumulated rather than unified, so we use the special `unifyBranches` function.
    and inferMatch fresh env clauses other =
        let unifyBranchFold (ty, constrs) next =
            let ret, moreConstrs = unifyBranches ty next
            ret, List.append moreConstrs constrs
        match other with
        | [] ->
            let (infCs, constrsCs, csExpand) = List.map (inferMatchClause fresh env) clauses |> List.unzip3
            let joinType, joinConstrs = List.fold unifyBranchFold (infCs.Head, []) infCs.Tail
            // TODO: update the inferred type here with possible match failure effect, since the else clause is elided
            joinType, List.concat [List.concat constrsCs; joinConstrs], [Syntax.EMatch (csExpand, [])]
        | otherExpr ->
            let (infOther, constrsOther, otherExpand) = inferExpr fresh env otherExpr
            let (infCs, constrsCs, csExpand) = List.map (inferMatchClause fresh env) clauses |> List.unzip3
            let joinType, joinConstrs = List.fold unifyBranchFold (infOther, []) infCs
            joinType, List.concat [constrsOther; List.concat constrsCs; joinConstrs], [Syntax.EMatch (csExpand, otherExpand)]

    /// Let statements are basically syntactic sugar for a single-branch `match` expression.
    /// Locals are a bit more complex, but generally behave like inferring a recursive function,
    /// with the notable absence of generalization post-inference.
    /// Compound expressions have the same inference as compound words, just composition.
    and inferBlock fresh env stmts =
        match stmts with
        | [] -> (freshIdentity fresh, [], [])
        | Syntax.SLet { Matcher = ps; Body = b } :: ss ->
            let (bTy, bCnstr, bPlc) = inferExpr fresh env b
            let varTypes, constrsP, poppedTypes = List.map (inferPattern fresh env) (DotSeq.toList ps |> List.rev) |> List.unzip3
            let varEnv = extendPushVars env (List.concat varTypes)
            let (ssTy, ssCnstr, ssPlc) = inferBlock fresh varEnv ss
            let popped = freshPopped fresh poppedTypes

            let (uniTyL, uniConstrL) = composeWordTypes bTy popped
            let (uniTyR, uniConstrR) = composeWordTypes uniTyL ssTy
            let shareConstrs = sharingAnalysis fresh (List.concat varTypes) [[Syntax.EStatementBlock ss]]
            (uniTyR, List.concat [bCnstr; List.concat constrsP; ssCnstr; uniConstrL; uniConstrR; shareConstrs],
                Syntax.SLet { Matcher = ps; Body = bPlc } :: ssPlc)
        | Syntax.SLocals _ :: ss -> failwith "Local functions not yet implemented."
        | Syntax.SExpression e :: ss ->
            let (eTy, eCnstr, ePlc) = inferExpr fresh env e
            let (sTy, sCnstr, sPlc) = inferBlock fresh env ss
            let (uniTy, uniConstrs) = composeWordTypes eTy sTy
            (uniTy, append3 eCnstr sCnstr uniConstrs, Syntax.SExpression ePlc :: sPlc)
    and inferHandle fresh env hdlParams body handlers after =
        assert (handlers.Length > 0)
        let (hdldTy, hdldCnstrs, hdldPlc) = inferBlock fresh env body
        let hdlrTypeTemplates = List.map (fun (h: Boba.Compiler.Syntax.Handler) -> (getWordEntry env h.Name.Name.Name).Type) handlers

        // get the basic effect row type of the effect
        let exHdlrType = instantiateExn fresh hdlrTypeTemplates.[0]
        let effRow = functionValueTypeEffect (qualTypeHead exHdlrType)
        let effCnstr = { Left = effRow; Right = functionValueTypeEffect (qualTypeHead hdldTy) }
        let effHdldTy =
            qualType
                (qualTypeContext hdldTy)
                (updateFunctionValueTypeEffect (qualTypeHead hdldTy) (rowTypeTail effRow))

        let psTypes = List.map (fun (p : Syntax.Name) -> (p.Name, freshValueComponentType fresh)) hdlParams
        let psEnv = extendPushVars env psTypes

        let (aftTy, aftCnstrs, aftPlc) = inferExpr fresh psEnv after
        let hdlResult = functionValueTypeOuts (qualTypeHead aftTy)
        let (hdlrTys, hdlrCnstrs, hdlrPlcs) = List.map (inferHandler fresh psEnv psTypes hdlResult) handlers |> List.unzip3

        let argPopped = freshPopped fresh (List.map snd psTypes)
        let hdlType, hdlCnstrs = composeWordTypes argPopped effHdldTy
        let finalTy, finalCnstrs = composeWordTypes hdlType aftTy
        let replaced = Syntax.EHandle (hdlParams, hdldPlc, hdlrPlcs, aftPlc)

        let sharedParamsCnstrs = sharingAnalysis fresh psTypes (after :: (List.map (fun (h: Boba.Compiler.Syntax.Handler) -> h.Body) handlers))

        finalTy, List.concat [finalCnstrs; hdlCnstrs; List.concat hdlrCnstrs; aftCnstrs; sharedParamsCnstrs; [effCnstr]; hdldCnstrs], [replaced]
    and inferHandler fresh env hdlParams resultTy hdlr =
        // TODO: this doesn't account for overloaded dictionary parameters yet
        let psTypes = List.map (fun (p: Syntax.Name) -> (p.Name, freshValueComponentType fresh)) hdlr.Params
        let psEnv = extendPushVars env psTypes
        let resumeTy = freshResume fresh (List.map snd hdlParams) resultTy
        let resEnv = extendFn psEnv "resume" { Quantified = []; Body = resumeTy }

        let (bInf, bCnstrs, bPlc) = inferExpr fresh resEnv hdlr.Body
        let argPopped = freshPopped fresh (List.map snd psTypes)
        let (hdlrTy, hdlrCnstrs) = composeWordTypes argPopped bInf

        let hdlrTemplate = freshResume fresh (List.map snd psTypes) resultTy
        let sharedParamsCnstrs = sharingAnalysis fresh psTypes [hdlr.Body]
        let templateCnstr = { Left = qualTypeHead hdlrTemplate; Right = qualTypeHead hdlrTy }
        hdlrTy, List.concat [[templateCnstr]; sharedParamsCnstrs; hdlrCnstrs; bCnstrs], { hdlr with Body = bPlc }
    and inferMatchClause fresh env clause =
        let varTypes, constrsP, poppedTypes =
            DotSeq.map (inferPattern fresh env) clause.Matcher
            |> DotSeq.toList
            |> List.rev
            |> List.unzip3

        let varTypes = List.concat varTypes
        let varEnv = extendPushVars env varTypes
        let bTy, bCnstr, bPlc = inferExpr fresh varEnv clause.Body
        let popped = freshPopped fresh poppedTypes
        let uniTy, uniConstr = composeWordTypes popped bTy
        let sharedCnstrs = sharingAnalysis fresh varTypes [clause.Body]
        uniTy, List.concat [bCnstr; List.concat constrsP; uniConstr; sharedCnstrs], { Matcher = clause.Matcher; Body = bPlc }
    and inferCaseClause fresh env caseShare clause =
        let infBody, constrsBody, bodyExp = inferExpr fresh env clause.Body
        let ne = freshEffectVar fresh
        let np = freshPermVar fresh
        let fs = freshShareVar fresh
        let fieldVal = mkValueType (freshDataVar fresh) fs
        let nr = freshFieldVar fresh
        let rest = freshSequenceVar fresh
        let i = typeValueSeq (DotSeq.ind (mkVariantValueType (mkFieldRowExtend clause.Tag.Name fieldVal nr) (typeOr fs caseShare)) rest)
        let o = typeValueSeq (DotSeq.ind fieldVal rest)
        let destruct = unqualType (mkExpressionType ne np totalAttr i o)
        let infDest, constrsDest = composeWordTypes destruct infBody
        infDest, List.append constrsBody constrsDest, { clause with Body = bodyExp }
    
    let testAmbiguous reduced normalized =
        // TODO: implement ambiguity test here
        //qualType reduced (qualTypeHead normalized)
        reduced

    let contextReduceExn fresh ty rules =
        let context, head = qualTypeComponents ty
        let context = DotSeq.toList context
        if List.isEmpty context
        then Map.empty, ty
        else
            let solved = CHR.solvePredicates fresh rules (Set.ofList context)
            if List.length solved > 1
            then failwith $"Non-confluent context detected, rule set should be investigated!"
            else
                let solvedContext, subst = solved.[0]
                let dotContext = DotSeq.ofList (Set.toList solvedContext)
                subst, typeSubstExn subst (qualType dotContext head)

    let inferTop fresh env expr =
        try
            let (inferred, constrs, expanded) = inferExpr fresh env expr
            let subst = solveAll fresh constrs
            let normalized = typeSubstExn subst inferred
            let redSubst, reduced = contextReduceExn fresh normalized (envRules env)
            (testAmbiguous reduced normalized, composeSubstExn redSubst subst, expanded)
        with
            | UnifyKindMismatch (t1, t2, k1, k2) -> failwith $"{t1}:{k1} kind mismatch with {t2}:{k2}"
            | UnifyRigidRigidMismatch (l, r) -> failwith $"{l} cannot unify with {r}"
    
    /// Generate a parameter list corresponding to the overload constraints of a function.
    /// So `Num a, Eq a => (List (List a)) (List a) --> bool` yields something like
    /// `[(Num? a, dict*2), (Eq? a, dict*1)]`, along with the elaboration of the function
    /// that takes the parameters in the proper order.
    let elaborateParams (fresh : FreshVars) ty exp =
        let ctx = qualTypeContext ty
        // TODO: this doesn't support dotted constraints yet!
        let indCtx = DotSeq.toList ctx
        // the '*' in the name for each dictionary variable ensures uniqueness, no need to handle shadowing
        let vars = [for c in indCtx -> fresh.Fresh "dict*"]
        let varPats = [for v in vars -> Syntax.PNamed (Syntax.stringToSmallName v, Syntax.PWildcard)]
        List.zip indCtx vars, [Syntax.EStatementBlock [Syntax.SLet { Matcher = DotSeq.ofList varPats; Body = [] }; Syntax.SExpression exp]]

    let smallIdentFromString s : Syntax.Identifier = { Qualifier = []; Name = Syntax.stringToSmallName s }

    let rec resolveOverload fresh env paramMap ty =
        let constr, arg = typeConstraintComponents ty
        let over = lookupPred env constr
        match List.tryFind (fun inst -> isTypeMatch fresh (qualTypeHead (fst inst).Body) arg) over.Instances with
        | Some (instTy, n) ->
            // TODO: this doesn't yet handle dotted constraints!
            let instConstrs = qualTypeContext instTy.Body |> DotSeq.toList
            let elaborateInst = List.collect (resolveOverload fresh env paramMap) instConstrs
            [Syntax.EFunctionLiteral (List.append elaborateInst [Syntax.EIdentifier (smallIdentFromString n)])]
        | None ->
            // no instance fits, which parameter fits?
            // TODO: need to use something stronger than type matching here, like syntactic equality
            // but maybe just syntactic equality on non-Boolean/non-Abelian types?
            match List.tryFind (fun (parType, _) -> isTypeMatch fresh arg (typeConstraintArg parType)) paramMap with
            | Some (_, parVar) -> [Syntax.EIdentifier (smallIdentFromString parVar)]
            | None -> failwith $"Could not resolve overload arg {ty}"

    let resolveMethod fresh env paramMap name ty =
        let over = env.Overloads.[name]
        // do we have an instance that fits?
        let fnSig = typeConstraintArg ty
        match List.tryFind (fun inst -> isTypeMatch fresh (qualTypeHead (fst inst).Body) fnSig) over.Instances with
        | Some (instTy, n) ->
            // TODO: this doesn't yet handle dotted constraints!
            let instConstrs = qualTypeContext instTy.Body |> DotSeq.toList
            let elaborateInst = List.collect (resolveOverload fresh env paramMap) instConstrs
            List.append elaborateInst [Syntax.EIdentifier (smallIdentFromString n)]
        | None ->
            // no instance fits, which parameter fits?
            // TODO: need to use something stronger than type matching here, like syntactic equality
            // but maybe just syntactic equality on non-Boolean/non-Abelian types?
            match List.tryFind (fun (parType, _) -> isTypeMatch fresh fnSig (typeConstraintArg parType)) paramMap with
            | Some (_, parVar) -> [Syntax.EIdentifier (smallIdentFromString parVar); Syntax.EDo]
            | None -> failwith $"Could not resolve method {name}"

    let resolveRecursive fresh env paramMap name ty = [Syntax.ERecursivePlaceholder (name, ty)]

    let rec elaboratePlaceholders fresh env subst paramMap paramExp =
        List.map (elaborateWord fresh env subst paramMap) paramExp
    and elaborateWord fresh env subst paramMap word =
        match word with
        | Syntax.EStatementBlock stmts -> Syntax.EStatementBlock (elaborateStmts fresh env subst paramMap stmts)
        | Syntax.EHandle (ps, hdld, hdlrs, r) ->
            Syntax.EHandle (ps, elaborateStmts fresh env subst paramMap hdld, List.map (elaborateHandler fresh env subst paramMap) hdlrs, elaboratePlaceholders fresh env subst paramMap r)
        | Syntax.EInject (ns, stmts) -> Syntax.EInject (ns, List.map (elaborateStmt fresh env subst paramMap) stmts)
        | Syntax.EMatch (cs, other) -> Syntax.EMatch (List.map (elaborateMatchClause fresh env subst paramMap) cs, elaboratePlaceholders fresh env subst paramMap other)
        | Syntax.EIf (c, t, e) -> Syntax.EIf (elaboratePlaceholders fresh env subst paramMap c, elaborateStmts fresh env subst paramMap t, elaborateStmts fresh env subst paramMap e)
        | Syntax.EWhile (c, b) -> Syntax.EWhile (elaboratePlaceholders fresh env subst paramMap c, elaborateStmts fresh env subst paramMap b)
        | Syntax.EFunctionLiteral e -> Syntax.EFunctionLiteral (elaboratePlaceholders fresh env subst paramMap e)
        | Syntax.ETupleLiteral (r) -> Syntax.ETupleLiteral (elaboratePlaceholders fresh env subst paramMap r)
        | Syntax.EListLiteral (r, es) -> Syntax.EListLiteral (elaboratePlaceholders fresh env subst paramMap r, List.map (elaboratePlaceholders fresh env subst paramMap) es)
        | Syntax.EVectorLiteral (r, es) -> Syntax.EVectorLiteral (elaboratePlaceholders fresh env subst paramMap r, List.map (elaboratePlaceholders fresh env subst paramMap) es)
        | Syntax.ERecordLiteral (r) -> Syntax.ERecordLiteral (elaboratePlaceholders fresh env subst paramMap r)
        | Syntax.EVariantLiteral (n, e) -> Syntax.EVariantLiteral (n, elaboratePlaceholders fresh env subst paramMap e)
        | Syntax.ECase (cs, o) -> Syntax.ECase (List.map (elaborateCase fresh env subst paramMap) cs, elaboratePlaceholders fresh env subst paramMap o)
        | Syntax.EWithPermission (n, stmts) -> Syntax.EWithPermission (n, elaborateStmts fresh env subst paramMap stmts)
        | Syntax.EWithState stmts -> Syntax.EWithState (elaborateStmts fresh env subst paramMap stmts)
        | Syntax.EMethodPlaceholder (name, ty) ->
            Syntax.EStatementBlock [Syntax.SExpression (resolveMethod fresh env paramMap name (typeSubstExn subst ty))]
        | Syntax.ERecursivePlaceholder (name, ty) ->
            Syntax.EStatementBlock [Syntax.SExpression (resolveRecursive fresh env paramMap name (typeSubstExn subst ty))]
        | Syntax.EOverloadPlaceholder ty ->
            Syntax.EStatementBlock [Syntax.SExpression (resolveOverload fresh env paramMap (typeSubstExn subst ty))]
        | _ -> word
    and elaborateStmts fresh env subst paramMap stmts = List.map (elaborateStmt fresh env subst paramMap) stmts
    and elaborateStmt fresh env subst paramMap stmt =
        match stmt with
        | Syntax.SLet matcher -> Syntax.SLet (elaborateMatchClause fresh env subst paramMap matcher)
        | Syntax.SLocals _ -> failwith $"Elaboration for local functions not yet implemented."
        | Syntax.SExpression exp -> Syntax.SExpression (elaboratePlaceholders fresh env subst paramMap exp)
    and elaborateMatchClause fresh env subst paramMap clause =
        { clause with Body = elaboratePlaceholders fresh env subst paramMap clause.Body }
    and elaborateHandler fresh env subst paramMap handler =
        { handler with Body = elaboratePlaceholders fresh env subst paramMap handler.Body }
    and elaborateCase fresh env subst paramMap case =
        { case with Body = elaboratePlaceholders fresh env subst paramMap case.Body }

    let elaborateOverload fresh env subst ty exp =
        let paramMap, paramExp = elaborateParams fresh ty exp
        elaboratePlaceholders fresh env subst paramMap paramExp

    let inferFunction fresh env (fn: Syntax.Function) =
        // TODO: add fixed params to env
        let (ty, subst, exp) = inferTop fresh env fn.Body
        let elabExp = elaborateOverload fresh env subst ty exp
        let genTy = schemeFromType (simplifyType ty)
        (genTy, { fn with Body = elabExp })

    let inferRecFuncs fresh env (fns: List<Syntax.Function>) =
        // TODO: add fixed params to env
        let emptyScheme q = { Quantified = []; Body = q }
        let recEnv = List.fold (fun tenv (fn : Syntax.Function) -> extendRec tenv fn.Name.Name (freshTransform fresh |> emptyScheme)) env fns
        let infTys, constrs, exps = List.map (fun (fn : Syntax.Function) -> inferExpr fresh recEnv fn.Body) fns |> List.unzip3
        let subst = solveAll fresh (List.concat constrs)
        let norms = List.map (typeSubstExn subst) infTys
        // TODO: implement context reduction here
        // let reduced = List.map (fun qt -> contextReduceExn fresh qt.Context recEnv) norms
        let substs, reduced = List.map (fun n -> contextReduceExn fresh n (envRules env)) norms |> List.unzip
        let compSubst = List.fold (fun l r -> composeSubstExn r l) subst substs
        zipWith (uncurry testAmbiguous) reduced norms, compSubst, exps

    /// Creates two types: the first used during pattern-match type inference (and which thus
    /// has no context component), the second when the constructor is used in an expression to
    /// create a new value.
    let mkConstructorTy fresh componentsAndResult =
        let argTypes = List.take (List.length componentsAndResult - 1) componentsAndResult
        let retType = mkValueType (List.last componentsAndResult) (freshShareVar fresh)
        assert (List.forall (fun t -> typeKindExn t = KValue) argTypes)
        assert (typeKindExn retType = KValue)

        let tySeq = typeValueSeq (DotSeq.ofList componentsAndResult)
        let rest = freshSequenceVar fresh
        let o = typeValueSeq (DotSeq.ind retType rest)
        let i = typeValueSeq (DotSeq.append (DotSeq.ofList argTypes) rest)
        let e = freshEffectVar fresh
        let p = freshPermVar fresh
        let fn = mkExpressionType e p totalAttr i o
        schemeFromType tySeq, schemeFromType (unqualType fn)
    
    let inferRecDataTypes fresh env (dts : List<Syntax.DataType>) =
        let dataKindTemplate (dt : Syntax.DataType) = List.foldBack (fun _ k -> karrow (freshKind fresh) k) dt.Params KData
        let dataTypeKinds = List.map dataKindTemplate dts
        let recEnv =
            dataTypeKinds
            |> List.zip dts
            |> List.fold (fun e (dt, k) -> addTypeCtor e dt.Name.Name k) env
        let inferDataType (dt: Syntax.DataType) = List.map (inferConstructorKinds fresh recEnv) dt.Constructors
        let dtCtorKinds, constrs, dtCtorArgs =
            List.map (inferDataType >> List.unzip3) dts |> List.unzip3
        let subst = List.concat constrs |> List.concat |> solveKindConstraints
        let dataTypeKinds = List.map (kindSubst subst) dataTypeKinds
        let dtCtorArgs = List.map (List.map (List.map (typeKindSubstExn subst))) dtCtorArgs
        for kind in dataTypeKinds do
            if not (Set.isEmpty (kindFree kind))
            then
                failwith $"Polymorphic kinds not yet supported but inferred kind {kind}"
        let tyEnv =
            dataTypeKinds
            |> List.zip dts
            |> List.fold (fun env (dt, k) -> addTypeCtor env dt.Name.Name k) recEnv
        let ctorTypes = List.map (List.map (mkConstructorTy fresh)) dtCtorArgs
        let ctorNames = List.map (fun (dt: Syntax.DataType) -> List.map (fun (c: Syntax.Constructor) -> c.Name.Name) dt.Constructors) dts
        let ctorEnv =
            ctorTypes
            |> List.zip ctorNames
            |> List.collect (uncurry List.zip)
            |> List.fold (fun env p -> extendCtor env (fst p) (fst (snd p)) (snd (snd p))) tyEnv
        ctorEnv

    let mkSimplification fnTy predName =
        let context, head = qualTypeComponents fnTy
        // TODO: support dots in CHRs... seems like it might be tricky
        let context = DotSeq.toList context
        CHR.simplification [typeConstraint predName head] [for c in context -> CHR.CPredicate c]

    /// Gets both the assumed instance function type and constructs a constraint handling rule from it.
    let getInstanceType fresh env overName predName template decl =
        match decl with
        | Syntax.DInstance (n, t, b) when overName = n.Name ->
            let instTy = kindAnnotateType fresh env t
            if isTypeMatch fresh template (qualTypeHead instTy)
            then [schemeFromType instTy, mkSimplification instTy predName]
            else failwith $"Instance type for {overName} did not match template: {template} ~> {instTy}"
        | _ -> []
    
    let genInstanceName (fresh : FreshVars) overName decl =
        match decl with
        | Syntax.DInstance (n, t, b) when overName = n.Name -> [fresh.Fresh overName]
        | _ -> []

    let getInstanceBody overName decl =
        match decl with
        | Syntax.DInstance (n, t, b) when overName = n.Name -> [b]
        | _ -> []

    let gatherInstances fresh env overName predName template decls =
        let instTypes, instRules = List.collect (getInstanceType fresh env overName predName template) decls |> List.unzip
        let instNames = List.collect (genInstanceName fresh overName) decls
        let instBodies = List.collect (getInstanceBody overName) decls
        let overloadType = schemeFromType (qualType (DotSeq.ind (typeConstraint predName template) DotSeq.SEnd) template)
        let rulesEnv = List.fold addRule env instRules
        overloadType, addOverload rulesEnv overName predName overloadType (List.zip instTypes instNames), List.zip instNames instBodies
    
    let rec inferDefs fresh env defs exps =
        match defs with
        | [] -> (env, exps)
        | Syntax.DFunc f :: ds ->
            let (ty, exp) = inferFunction fresh env f
            inferDefs fresh (extendFn env f.Name.Name ty) ds (Syntax.DFunc exp :: exps)
        | Syntax.DRecFuncs fs :: ds ->
            let tys, subst, recExps = inferRecFuncs fresh env fs
            // TODO: generate placeholder parameters from context types
            let recFns = zipWith (fun (fn : Syntax.Function, exp) -> { fn with Body = elaboratePlaceholders fresh env subst [] exp }) fs recExps
            let newEnv =
                Syntax.declNames (Syntax.DRecFuncs fs)
                |> Syntax.namesToStrings
                |> Seq.zip (Seq.map (simplifyType >> schemeFromType) tys)
                |> Seq.fold (fun env nt -> extendFn env (snd nt) (fst nt)) env
            inferDefs fresh newEnv ds (Syntax.DRecFuncs recFns :: exps)
        | Syntax.DCheck c :: ds ->
            match lookup env c.Name.Name with
            | Some entry ->
                let general = instantiateExn fresh entry.Type
                let matcher = kindAnnotateType fresh env c.Matcher
                // TODO: also check that the contexts match or are a subset
                if isTypeMatch fresh (qualTypeHead general) (qualTypeHead matcher)
                // TODO: should we continue to use the inferred (more general) type, or restrict it to
                // be the quantified asserted type?
                then inferDefs fresh env ds (Syntax.DCheck c :: exps)
                else failwith $"Type of '{c.Name}' did not match it's assertion.\n{general} ~> {matcher}"
            | None -> failwith $"Could not find name '{c.Name}' to check its type."
        | Syntax.DEffect e :: ds ->
            // TODO: fix kind to allow effects with params here
            let effTyEnv = addTypeCtor env e.Name.Name KEffect
            let hdlrTys = List.map (fun (h: Syntax.HandlerTemplate) -> (h.Name.Name, schemeFromType (kindAnnotateType fresh effTyEnv h.Type))) e.Handlers
            let effEnv = Seq.fold (fun env nt -> extendFn env (fst nt) (snd nt)) effTyEnv hdlrTys
            inferDefs fresh effEnv ds (Syntax.DEffect e :: exps)
        | Syntax.DType d :: ds ->
            let dataTypeEnv = inferRecDataTypes fresh env [d]
            inferDefs fresh dataTypeEnv ds (Syntax.DType d :: exps)
        | Syntax.DRecTypes dts :: ds ->
            let dataTypeEnv = inferRecDataTypes fresh env dts
            inferDefs fresh dataTypeEnv ds (Syntax.DRecTypes dts :: exps)
        | Syntax.DOverload (n, p, t, _) :: ds ->
            let overFn = kindAnnotateType fresh env t
            let overType, overEnv, overBodies = gatherInstances fresh env n.Name p.Name overFn ds
            // TODO: gather related rules here
            inferDefs fresh (extendOver overEnv n.Name overType) ds (Syntax.DOverload (n, p, t, overBodies) :: exps)
        | Syntax.DInstance (n, t, b) :: ds ->
            let instTemplate = kindAnnotateType fresh env t
            let ty, subst, exp = inferTop fresh env b
            if isTypeMatch fresh (qualTypeHead instTemplate) (qualTypeHead ty)
            then inferDefs fresh env ds (Syntax.DInstance (n, t, exp) :: exps)
            else failwith $"Type of '{n.Name}' did not match it's assertion.\n{ty} ~> {instTemplate}"
        | Syntax.DTest t :: ds ->
            // tests are converted to functions before TI in test mode, see TestGenerator
            printfn $"Skipping type inference for test {t.Name.Name}, TI for tests will only run in test mode."
            inferDefs fresh env ds (Syntax.DTest t :: exps)
        | Syntax.DLaw t :: ds ->
            // laws are converted to functions before TI in test mode, see TestGenerator
            printfn $"Skipping type inference for law {t.Name.Name}, TI for laws will only run in test mode."
            inferDefs fresh env ds (Syntax.DLaw t :: exps)
        | Syntax.DTag (tagTy, tagTerm) :: ds ->
            let tagEnv = extendVar env tagTerm.Name (schemeFromType (typeCon tagTy.Name KUnit))
            inferDefs fresh tagEnv ds (Syntax.DTag (tagTy, tagTerm) :: exps)
        | d :: ds -> failwith $"Inference for declaration {d} not yet implemented."
    
    let inferProgram prog =
        let fresh = SimpleFresh(0)
        let (env, expanded) = inferDefs fresh Primitives.primTypeEnv prog.Declarations []
        let (mType, subst, mainExpand) = inferTop fresh env prog.Main
        // TODO: compile option for enforcing totality? right now we infer it but don't enforce it in any way
        // TODO: compile option for enforcing no unhandled effects? we infer them but don't yet check for this
        let mainTemplate = freshPush fresh (freshTotalVar fresh) (freshIntValueType fresh I32)
        if isTypeMatch fresh (qualTypeHead mainTemplate) (qualTypeHead mType)
        then { Declarations = expanded; Main = mainExpand }, env
        else failwith $"Main expected to have type {mainTemplate}, but had type {mType}"