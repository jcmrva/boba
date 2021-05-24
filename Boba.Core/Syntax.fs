﻿namespace Boba.Core

module Syntax =

    open Types

    type Word =
        | WHandle of pars: List<string> * handled: Expression * handlers: List<Handler> * ret: Expression
        | WIfStruct of ctor: string * thenClause: Expression * elseClause: Expression
        | WIf of thenClause: Expression * elseClause: Expression
        | WWhile of cond: Expression * body: Expression
        | WVars of vars: List<string> * body: Expression

        | WClosure of free: List<string> * body: string
        | WRecClosure of free: List<string> * body: string

        | WExtension of string
        | WRestriction of string
        | WSelect of string

        | WVariantLiteral of string
        | WEmbedding of string
        | WCase of tag: string * thenClause: Expression * elseClause: Expression

        | WWithPermission of string * Expression

        | WNewRef
        | WGetRef
        | WPutRef

        | WDo
        | WString of string
        | WInteger of string * IntegerSize
        | WDecimal of string * FloatSize
        | WChar of char

        | WPrimVar of string
        | WCallVar of string
        | WValueVar of string
        | WOperatorVar of string
        | WConstructorVar of string
        | WTestConstructorVar of string
    and Expression = List<Word>
    and Handler = { Name: string; Params: List<string>; Body: Expression }

    let rec wordFree w =
        match w with
        | WHandle (p, h, hs, r) ->
            let handlersFree = Set.difference (Set.union (Set.unionMany (List.map handlerFree hs)) (exprFree r)) (Set.ofList p)
            Set.union handlersFree (exprFree h)
        | WIf (t, e) -> Set.union (exprFree t) (exprFree e)
        | WWhile (t, e) -> Set.union (exprFree t) (exprFree e)
        | WVars (v, e) -> Set.difference (exprFree e) (Set.ofList v)
        | WClosure (c, b) -> Set.ofList c
        | WRecClosure (c, b) -> Set.remove b (Set.ofList c)
        | WCase (_, t, e) -> Set.union (exprFree t) (exprFree e)
        | WWithPermission (_, e) -> exprFree e
        | WValueVar n -> Set.singleton n
        | _ -> Set.empty
    and exprFree e =
        Set.unionMany (List.map wordFree e)
    and handlerFree h = Set.difference (exprFree h.Body) (Set.ofList h.Params)