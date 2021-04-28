// Signature file for parser generated by fsyacc
module Parser
type token = 
  | PATTERN
  | RECURSIVE
  | DATA
  | MAIN
  | EXPORT
  | AS
  | IMPORT
  | EQUALS
  | ELLIPSIS
  | DOT
  | PLUS
  | DOUBLE_COLON
  | COLON
  | R_BRACKET
  | L_BRACKET
  | R_BRACE
  | L_BRACE
  | R_PAREN
  | L_PAREN
  | R_ANGLE
  | L_ANGLE
  | DECIMAL of (string)
  | INTEGER of (string)
  | PROPERTY_NAME of (string)
  | PREDICATE_NAME of (string)
  | OPERATOR_NAME of (string)
  | BIG_NAME of (string)
  | SMALL_NAME of (string)
type tokenId = 
    | TOKEN_PATTERN
    | TOKEN_RECURSIVE
    | TOKEN_DATA
    | TOKEN_MAIN
    | TOKEN_EXPORT
    | TOKEN_AS
    | TOKEN_IMPORT
    | TOKEN_EQUALS
    | TOKEN_ELLIPSIS
    | TOKEN_DOT
    | TOKEN_PLUS
    | TOKEN_DOUBLE_COLON
    | TOKEN_COLON
    | TOKEN_R_BRACKET
    | TOKEN_L_BRACKET
    | TOKEN_R_BRACE
    | TOKEN_L_BRACE
    | TOKEN_R_PAREN
    | TOKEN_L_PAREN
    | TOKEN_R_ANGLE
    | TOKEN_L_ANGLE
    | TOKEN_DECIMAL
    | TOKEN_INTEGER
    | TOKEN_PROPERTY_NAME
    | TOKEN_PREDICATE_NAME
    | TOKEN_OPERATOR_NAME
    | TOKEN_BIG_NAME
    | TOKEN_SMALL_NAME
    | TOKEN_end_of_input
    | TOKEN_error
type nonTerminalId = 
    | NONTERM__startunit
    | NONTERM_unit
/// This function maps tokens to integer indexes
val tagOfToken: token -> int

/// This function maps integer indexes to symbolic token ids
val tokenTagToTokenId: int -> tokenId

/// This function maps production indexes returned in syntax errors to strings representing the non terminal that would be produced by that production
val prodIdxToNonTerminal: int -> nonTerminalId

/// This function gets the name of a token as a string
val token_to_string: token -> string
val unit : (FSharp.Text.Lexing.LexBuffer<'cty> -> token) -> FSharp.Text.Lexing.LexBuffer<'cty> -> ( int ) 
