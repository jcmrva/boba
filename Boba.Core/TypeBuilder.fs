﻿namespace Boba.Core

module TypeBuilder =

    open DotSeq
    open Kinds
    open Types
    open Fresh

    let DataVarPrefix = "d"
    let TrustedVarPrefix = "v"
    let ShareVarPrefix = "s"
    let ClearanceVarPrefix = "k"
    let EffVarPrefix = "e"
    let HeapVarPrefix = "h"
    let PermVarPrefix = "p"
    let TotalVarPrefix = "q"
    let FieldVarPrefix = "f"
    let FixedVarPrefix = "x"
    let UnitVarPrefix = "u"
    let ValueVarPrefix = "t"
    let RowVarPrefix = "r"
    let CtorVarPrefix = "c"
    let SeqVarPrefix = "z"

    let typeVarPrefix kind =
        match kind with
        | KData -> DataVarPrefix
        | KTrust -> TrustedVarPrefix
        | KSharing -> ShareVarPrefix
        | KClearance -> ClearanceVarPrefix
        | KHeap -> HeapVarPrefix
        | KTotality -> TotalVarPrefix
        | KFixed -> FixedVarPrefix
        | KUnit -> UnitVarPrefix
        | KValue -> ValueVarPrefix
        | KRow KEffect -> EffVarPrefix
        | KRow KPermission -> PermVarPrefix
        | KRow KField -> FieldVarPrefix
        | KArrow _ -> CtorVarPrefix
        | KSeq _ -> SeqVarPrefix
        | _ -> failwith "Tried to get prefix for non-var kind"

    let mkTypeVar ext kind = typeVar ((typeVarPrefix kind) + ext) kind

    /// Create a variable with the given name and kind `KTrust`
    let trustVar name = typeVar name KTrust
    /// Create a variable with the given name and kind `KSharing`
    let shareVar name = typeVar name KSharing
    /// Create a variable with the given name and kind `KClearance`
    let clearVar name = typeVar name KClearance
    /// Create a variable with the given name and kind `KUnit`
    let unitVar name = typeVar name KUnit

    /// All value types in Boba have four components:
    /// 1) the data representation/format (inner, kind `Data`)
    /// 2) the trust attribute (middle inner, kind `Trust`)
    /// 3) the clearance attributed (middle outer, kind `Clearance`)
    /// 4) the sharing attribute (outer, kind `Sharing`)
    /// 
    /// Each of these components has a different kind to distinguish them
    /// during type inference/checking, to improve separation and prevent
    /// mistakes in the implementation of inference/checking, and to drive
    /// unification during type inference.
    let mkValueType data trust clearance sharing =
        assert (typeKindExn data = KData)
        assert (typeKindExn trust = KTrust)
        assert (typeKindExn clearance = KClearance)
        assert (typeKindExn sharing = KSharing)
        typeApp (typeApp (typeApp (typeApp (TPrim PrValue) data) trust) clearance) sharing

    let valueTypeComponents ty =
        match ty with
        | TApp (TApp (TApp (TApp (TPrim PrValue, data), trust), clearance), sharing) ->
            {| Data = data; Trust = trust; Clearance = clearance; Sharing = sharing |}
        | _ -> failwith $"Could not extract value type components from type {ty}"
    
    /// Extract the data component from a value type.
    let valueTypeData ty = (valueTypeComponents ty).Data

    /// Extract the trust attribute component from a value type.
    let valueTypeTrust ty = (valueTypeComponents ty).Trust

    /// Extract the clearance attribute component from a value type.
    let valueTypeClearance ty = (valueTypeComponents ty).Clearance

    /// Extract the sharing attribute component from a value type.
    let valueTypeSharing ty = (valueTypeComponents ty).Sharing

    let updateValueTypeData ty data =
        mkValueType data (valueTypeTrust ty) (valueTypeClearance ty) (valueTypeSharing ty)

    let updateValueTypeSharing ty sharing =
        mkValueType (valueTypeData ty) (valueTypeTrust ty) (valueTypeClearance ty) sharing

    /// Function types are the meat and potatoes of Boba, the workhorse
    /// that encodes a lot of the interesting information about a function
    /// and what it does. All function types are value types and so share
    /// the components of a value type, but the data component is the most
    /// interesting in how it differs from other value types. Each function
    /// type data component in Boba has 5 sub-components:
    /// 1) The effects actioned but not handled by the function expression (inner, kind Effect)
    /// 2) The permissions required but not dispatched by the function expression (2nd, kind Permission)
    /// 3) The totality (whether the function is known to always terminate) (3rd, kind Totality)
    /// 4) The input value sequence required for the expression to complete (4th, kind Seq Value)
    /// 5) The output value sequence returned when the expression completes (outer, kind Seq Value)
    /// 
    /// For the other three attributes, a couple special conditions apply:
    /// 1) The trust attribute is almost always Trusted (True), since user input can never contruct
    ///    a function value. While closures may have access to Untrusted data, that does not mean
    ///    that the function itself is Untrusted. The only time a function type is Untrusted is when
    ///    a module is imported as untrusted or when a function value comes from a dynamically loaded library.
    /// 2) The clearance attribute is dependent on the values the function closure contains. This is to
    ///    support eventual remote code execution on potentially untrusted machines, i.e. sending a function
    ///    to another machine to be computed and receiving the result. In such a case, we may not want to
    ///    expose sensitive information to the remote machine, but the closure may store sensitive data
    ///    in it's captured scope. So, a closure that refers to any variable marked secret will also be
    ///    be marked secret.
    /// 3) The sharing attribute, unlike trust, is dependent on the values the function closure contains.
    ///    So a closure value that refers to a value variable marked as unique must also be marked as unique.
    let mkFunctionType effs perms total ins outs =
        typeApp (typeApp (typeApp (typeApp (typeApp (TPrim PrFunction) effs) perms) total) ins) outs
    
    /// Convenience function for defining a function value type in one call.
    let mkFunctionValueType effs perms total ins outs trust clearance sharing =
        mkValueType (mkFunctionType effs perms total ins outs) trust clearance sharing

    /// Expressions almost always have fixed attributes: trusted, cleared, and shared.
    /// When they don't, we manually override them in specific scenarios as part of
    /// closure typing. So this convenience function is used in most of inference, which
    /// doesn't do a lot with closures.
    let mkExpressionType effs perms total ins outs =
        mkFunctionValueType effs perms total ins outs trustedAttr clearAttr sharedAttr
    
    let isFunctionValueType ty =
        match (valueTypeData ty) with
        | TApp (TApp (TApp (TApp (TApp (TPrim PrFunction, e), p), t), i), o) -> true
        | _ -> false

    /// Extract all the function data type components of the function value type.
    /// 0. Effect component
    /// 1. Permission component
    /// 2. Totality component
    /// 3. Input sequence
    /// 4. Output sequence
    let functionValueTypeComponents fnTy =
        match (valueTypeData fnTy) with
        | TApp (TApp (TApp (TApp (TApp (TPrim PrFunction, e), p), t), i), o) -> (e, p, t, i, o)
        | _ -> failwith "Could not extract function type components, not a valid function value type."
    
    let functionValueTypeEffect fnTy =
        match functionValueTypeComponents fnTy with
        | (e, _, _, _, _) -> e

    let functionValueTypeIns fnTy =
        match functionValueTypeComponents fnTy with
        | (_, _, _, is, _) -> is
    
    let functionValueTypeOuts fnTy =
        match functionValueTypeComponents fnTy with
        | (_, _, _, _, os) -> os

    let updateFunctionValueTypeEffect fnTy eff =
        let (_, p, t, i, o) = functionValueTypeComponents fnTy
        updateValueTypeData fnTy (mkFunctionType eff p t i o)

    let mkStringValueType trust clearance sharing =
        mkValueType (TPrim PrString) trust clearance sharing

    let mkListType elem trust clearance sharing =
        mkValueType (typeApp (TPrim PrList) elem) trust clearance sharing

    let mkRowExtend elem row =
        typeApp (typeApp (TRowExtend (typeKindExn elem)) elem) row

    let mkFieldRowExtend name elem row = mkRowExtend (typeField name elem) row
    
    let mkRecordValueType row trust clearance sharing =
        mkValueType (typeApp (TPrim PrRecord) row) trust clearance sharing
    
    let mkVariantValueType row trust clearance sharing =
        mkValueType (typeApp (TPrim PrVariant) row) trust clearance sharing

    let mkRefValueType heap elem trust clearance sharing =
        mkValueType (typeApp (typeApp (TPrim PrRef) heap) elem) trust clearance sharing
    
    let rowTypeTail row =
        match row with
        | TApp (TApp (TRowExtend _, _), tail) -> tail
        | _ -> failwith $"Expected row type with one element head, but got {row}"

    let schemeSharing sch = valueTypeSharing sch.Body.Head

    let schemeClearance sch = valueTypeClearance sch.Body.Head

    let schemeFromQual qType =
        { Quantified = qualFreeWithKinds qType |> Set.toList; Body = qType }

    let freshTypeVar (fresh : FreshVars) kind = typeVar (fresh.Fresh (typeVarPrefix kind)) kind
    let freshDataVar fresh = freshTypeVar fresh KData
    let freshTrustVar fresh = freshTypeVar fresh KTrust
    let freshClearVar fresh = freshTypeVar fresh KClearance
    let freshShareVar fresh = freshTypeVar fresh KSharing
    let freshValueVar fresh = freshTypeVar fresh KValue
    let freshUnitVar fresh = freshTypeVar fresh KUnit
    let freshEffectVar fresh = freshTypeVar fresh (KRow KEffect)
    let freshFieldVar fresh = freshTypeVar fresh (KRow KField)
    let freshHeapVar fresh = freshTypeVar fresh KHeap
    let freshPermVar fresh = freshTypeVar fresh (KRow KPermission)
    let freshTotalVar fresh = freshTypeVar fresh KTotality
    let freshSequenceVar fresh = SDot (freshTypeVar fresh KValue, SEnd)

    let freshValueComponentType fresh =
        mkValueType (freshDataVar fresh) (freshTrustVar fresh) (freshClearVar fresh) (freshShareVar fresh)

    let freshFunctionAttributes (fresh : FreshVars) =
        (freshEffectVar fresh, freshPermVar fresh, freshTotalVar fresh)

    let freshFloatType fresh floatSize = typeApp (TPrim (PrFloat floatSize)) (freshUnitVar fresh)
    let freshIntType fresh intSize = typeApp (TPrim (PrInteger intSize)) (freshUnitVar fresh)

    let freshFloatValueType fresh floatSize trust clearance =
        mkValueType (freshFloatType fresh floatSize) trust clearance (freshShareVar fresh)
    let freshIntValueType fresh intSize trust clearance =
        mkValueType (freshIntType fresh intSize) trust clearance (freshShareVar fresh)
    let freshStringValueType fresh trust clearance =
        mkValueType (TPrim PrString) trust clearance (freshShareVar fresh)
    let freshBoolValueType fresh trust clearance =
        mkValueType (TPrim PrBool) trust clearance (freshShareVar fresh)