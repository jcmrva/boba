namespace Boba.Core

module Abelian =

    open System.Diagnostics
    open Common

    /// Represents a simple Abelian equation composed of constant values and variables which can each have a signed integer exponent.
    /// The implementation uses dictionaries as a form of signed multiset, where an element in the set can have more than one occurence
    /// (represented by a positive exponent) or even negative occurences (represented by a negative exponent). If an element has exactly zero
    /// occurences, it is removed from the dictionary for efficiency.
    [<DebuggerDisplay("{ToString()}")>]
    type Equation<'a, 'b> when 'a: comparison and 'b: comparison (variables: Map<'a, int>, constants: Map<'b, int>) =

        new(name: 'a) =
            Equation<'a, 'b>(Map.add name 1 Map.empty, Map.empty)

        new() =
            Equation<'a, 'b>(Map.empty, Map.empty)

        /// The set of variables in the unit equation, mapped to their exponents.
        member this.Variables = variables
        /// The set of constants in the unit equation, mapped to their exponents.
        member this.Constants = constants

        member this.IsIdentity () = this.Variables.IsEmpty && this.Constants.IsEmpty

        member this.IsConstant () = this.Variables.IsEmpty

        /// Get the exponent of the given variable name within this equation. Returns 0 if the variable is not present.
        member this.ExponentOf var =
            if this.Variables.ContainsKey(var)
            then this.Variables.[var]
            else 0

        /// Determines whether all exponents in the equation are integer multiples of the given divisor.
        member this.DividesPowers divisor =
            let divides k pow = pow % divisor = 0
            Map.forall divides this.Variables && Map.forall divides this.Constants

        /// For a given variable, returns whether there is another variable in the equation that has a higher exponent.
        member this.NotMax var =
            let examined = this.ExponentOf var |> abs
            let varHasGreaterExponent k v = k <> var && abs v >= examined
            Map.exists varHasGreaterExponent this.Variables

        /// Negates each exponent in the equation.
        member this.Invert () =
            new Equation<'a, 'b>(mapValues (( ~- )) this.Variables, mapValues (( ~- )) this.Constants)

        /// Combine two Abelian unit equations. Values that appear in both equations have their exponents multiplied.
        member this.Add (other: Equation<'a, 'b>) =
            let mergeAdd (v1, v2) = v1 + v2
            let isExponentNonZero _ v = v <> 0
            let vars = mapUnion mergeAdd this.Variables other.Variables |> Map.filter isExponentNonZero
            let constants = mapUnion mergeAdd this.Constants other.Constants |> Map.filter isExponentNonZero
            new Equation<'a, 'b>(vars, constants)

        /// Removes the given equation from this Abelian unit equation. Equivalent to `this.Add(other.Invert())`.
        member this.Subtract (other: Equation<'a, 'b>) = other.Invert() |> this.Add;

        /// Multiplies each exponent in the unit equation by the given factor.
        member this.Scale (factor: int) =
            let scale v = v * factor
            new Equation<'a, 'b>(mapValues scale this.Variables, mapValues scale this.Constants)

        /// Divides each exponent in the unit equation by the given factor.
        member this.Divide (factor: int) =
            let scale v = v / factor
            new Equation<'a, 'b>(mapValues scale this.Variables, mapValues scale this.Constants)

        /// Removes the specified variable from the unit, and divides all other powers by the removed variable's power.
        member this.Pivot (var: 'a) =
            let pivotPower = this.ExponentOf var
            let inverse = new Equation<'a, 'b>(var)
            this.Subtract(inverse.Scale(pivotPower))
                .Divide(pivotPower)
                .Invert();

        // Get the free variables of this equation.
        member this.Free () = mapKeys this.Variables

        /// Substitutes the given unit for the specified variable, applying the variable's power to the substituted unit.
        member this.Substitute (name: 'a) (other: Equation<'a, 'b>) =
            other.Subtract(new Equation<'a, 'b>(name))
                .Scale(this.ExponentOf(name))
                .Add(this);

        override this.GetHashCode() =
            hash (this.Variables, this.Constants)

        override this.Equals(b) =
            match b with
            | :? Equation<'a, 'b> as p -> (this.Variables, this.Constants) = (p.Variables, p.Constants)
            | _ -> false

        override this.ToString() =
            if this.IsIdentity()
            then "-"
            else
                let vars = Map.map (fun k v -> $"{k}^{v}") this.Variables |> Map.toList |> List.map snd
                let cons = Map.map (fun k v -> $"{k}^{v}") this.Constants |> Map.toList |> List.map snd
                String.concat "*" (List.append vars cons)