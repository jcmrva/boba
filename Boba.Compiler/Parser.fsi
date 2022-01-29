// Signature file for parser generated by fsyacc
module Parser
type token = 
  | IS
  | ONE
  | TRUE
  | FALSE
  | AND
  | OR
  | NOT
  | TUPLE
  | LIST
  | VECTOR
  | SLICE
  | DICTIONARY
  | CASE
  | RECORD
  | VARIANT
  | FOR
  | BREAK
  | FINAL
  | FILL
  | LENGTH
  | RESULT
  | IF
  | WHEN
  | SWITCH
  | WHILE
  | THEN
  | ELSE
  | DO
  | MATCH
  | INJECT
  | WITH
  | AFTER
  | HANDLE
  | UNTAG
  | BY
  | PER
  | TRUST
  | DISTRUST
  | AUDIT
  | NEW_REF
  | GET_REF
  | PUT_REF
  | WITH_STATE
  | WITH_PERMISSION
  | FUNCTION
  | LOCAL
  | LET
  | IS_ROUGHLY
  | IS_NOT
  | SATISFIES
  | VIOLATES
  | TEST
  | LAW
  | EXHAUSTIVE
  | SYNONYM
  | TAG
  | EFFECT
  | OVERLOAD
  | INSTANCE
  | RULE
  | CHECK
  | PATTERN
  | RECURSIVE
  | DATA
  | MAIN
  | EXPORT
  | FROM
  | AS
  | IMPORT
  | REF
  | UNDERSCORE
  | EQUALS
  | ELLIPSIS
  | BAR
  | DOUBLE_BAR
  | DOT
  | PLUS
  | MINUS
  | STAR
  | COLON
  | DOUBLE_COLON
  | COMMA
  | SEMICOLON
  | FN_CTOR
  | L_BIND
  | R_BIND
  | L_STAR
  | R_STAR
  | L_ARROW
  | R_ARROW
  | L_BRACKET
  | R_BRACKET
  | L_BRACE
  | R_BRACE
  | L_PAREN
  | R_PAREN
  | L_ANGLE
  | R_ANGLE
  | STRING of (StringLiteral)
  | DECIMAL of (DecimalLiteral)
  | INTEGER of (IntegerLiteral)
  | TEST_NAME of (Name)
  | PREDICATE_NAME of (Name)
  | OPERATOR_NAME of (Name)
  | BIG_NAME of (Name)
  | SMALL_NAME of (Name)
  | EOF
type tokenId = 
    | TOKEN_IS
    | TOKEN_ONE
    | TOKEN_TRUE
    | TOKEN_FALSE
    | TOKEN_AND
    | TOKEN_OR
    | TOKEN_NOT
    | TOKEN_TUPLE
    | TOKEN_LIST
    | TOKEN_VECTOR
    | TOKEN_SLICE
    | TOKEN_DICTIONARY
    | TOKEN_CASE
    | TOKEN_RECORD
    | TOKEN_VARIANT
    | TOKEN_FOR
    | TOKEN_BREAK
    | TOKEN_FINAL
    | TOKEN_FILL
    | TOKEN_LENGTH
    | TOKEN_RESULT
    | TOKEN_IF
    | TOKEN_WHEN
    | TOKEN_SWITCH
    | TOKEN_WHILE
    | TOKEN_THEN
    | TOKEN_ELSE
    | TOKEN_DO
    | TOKEN_MATCH
    | TOKEN_INJECT
    | TOKEN_WITH
    | TOKEN_AFTER
    | TOKEN_HANDLE
    | TOKEN_UNTAG
    | TOKEN_BY
    | TOKEN_PER
    | TOKEN_TRUST
    | TOKEN_DISTRUST
    | TOKEN_AUDIT
    | TOKEN_NEW_REF
    | TOKEN_GET_REF
    | TOKEN_PUT_REF
    | TOKEN_WITH_STATE
    | TOKEN_WITH_PERMISSION
    | TOKEN_FUNCTION
    | TOKEN_LOCAL
    | TOKEN_LET
    | TOKEN_IS_ROUGHLY
    | TOKEN_IS_NOT
    | TOKEN_SATISFIES
    | TOKEN_VIOLATES
    | TOKEN_TEST
    | TOKEN_LAW
    | TOKEN_EXHAUSTIVE
    | TOKEN_SYNONYM
    | TOKEN_TAG
    | TOKEN_EFFECT
    | TOKEN_OVERLOAD
    | TOKEN_INSTANCE
    | TOKEN_RULE
    | TOKEN_CHECK
    | TOKEN_PATTERN
    | TOKEN_RECURSIVE
    | TOKEN_DATA
    | TOKEN_MAIN
    | TOKEN_EXPORT
    | TOKEN_FROM
    | TOKEN_AS
    | TOKEN_IMPORT
    | TOKEN_REF
    | TOKEN_UNDERSCORE
    | TOKEN_EQUALS
    | TOKEN_ELLIPSIS
    | TOKEN_BAR
    | TOKEN_DOUBLE_BAR
    | TOKEN_DOT
    | TOKEN_PLUS
    | TOKEN_MINUS
    | TOKEN_STAR
    | TOKEN_COLON
    | TOKEN_DOUBLE_COLON
    | TOKEN_COMMA
    | TOKEN_SEMICOLON
    | TOKEN_FN_CTOR
    | TOKEN_L_BIND
    | TOKEN_R_BIND
    | TOKEN_L_STAR
    | TOKEN_R_STAR
    | TOKEN_L_ARROW
    | TOKEN_R_ARROW
    | TOKEN_L_BRACKET
    | TOKEN_R_BRACKET
    | TOKEN_L_BRACE
    | TOKEN_R_BRACE
    | TOKEN_L_PAREN
    | TOKEN_R_PAREN
    | TOKEN_L_ANGLE
    | TOKEN_R_ANGLE
    | TOKEN_STRING
    | TOKEN_DECIMAL
    | TOKEN_INTEGER
    | TOKEN_TEST_NAME
    | TOKEN_PREDICATE_NAME
    | TOKEN_OPERATOR_NAME
    | TOKEN_BIG_NAME
    | TOKEN_SMALL_NAME
    | TOKEN_EOF
    | TOKEN_end_of_input
    | TOKEN_error
type nonTerminalId = 
    | NONTERM__startunit
    | NONTERM_unit
    | NONTERM_import_list
    | NONTERM_decl_list
    | NONTERM_main
    | NONTERM_import
    | NONTERM_import_path
    | NONTERM_remote
    | NONTERM_export
    | NONTERM_brace_names
    | NONTERM_name_list
    | NONTERM_name
    | NONTERM_declaration
    | NONTERM_function
    | NONTERM_function_list
    | NONTERM_datatype
    | NONTERM_datatype_list
    | NONTERM_constructor
    | NONTERM_constructor_list
    | NONTERM_rule
    | NONTERM_overload
    | NONTERM_instance
    | NONTERM_effect
    | NONTERM_handler_template_list
    | NONTERM_handler_template
    | NONTERM_test
    | NONTERM_test_all
    | NONTERM_test_is
    | NONTERM_check
    | NONTERM_tag
    | NONTERM_qual_type
    | NONTERM_predicate_list
    | NONTERM_predicate
    | NONTERM_any_type
    | NONTERM_any_type_list
    | NONTERM_term_statement_block
    | NONTERM_term_statement_list
    | NONTERM_term_statement
    | NONTERM_local_function_list
    | NONTERM_local_function
    | NONTERM_simple_expr
    | NONTERM_simple_expr_list
    | NONTERM_word
    | NONTERM_with_permission
    | NONTERM_handle_word
    | NONTERM_handler
    | NONTERM_return
    | NONTERM_param_list
    | NONTERM_handler_list
    | NONTERM_inject_word
    | NONTERM_eff_list
    | NONTERM_match_word
    | NONTERM_match_clause_list
    | NONTERM_match_clause
    | NONTERM_if_word
    | NONTERM_switch_word
    | NONTERM_switch_clause_list
    | NONTERM_when_word
    | NONTERM_while_word
    | NONTERM_function_literal
    | NONTERM_tuple_literal
    | NONTERM_list_literal
    | NONTERM_vector_literal
    | NONTERM_slice_literal
    | NONTERM_record_literal
    | NONTERM_variant_literal
    | NONTERM_case_word
    | NONTERM_case_clause_list
    | NONTERM_case_clause
    | NONTERM_field_list
    | NONTERM_field
    | NONTERM_identifier
    | NONTERM_qualified_name
    | NONTERM_qualified_ctor
    | NONTERM_no_dot_pattern_expr_list
    | NONTERM_pattern_expr_list
    | NONTERM_field_pattern_list
    | NONTERM_pattern_expr
    | NONTERM_tuple_pattern
    | NONTERM_list_pattern
    | NONTERM_vector_pattern
    | NONTERM_slice_pattern
    | NONTERM_record_pattern
    | NONTERM_fixed_size_term_expr
    | NONTERM_fixed_size_term_factor_list
    | NONTERM_fixed_size_term_factor
/// This function maps tokens to integer indexes
val tagOfToken: token -> int

/// This function maps integer indexes to symbolic token ids
val tokenTagToTokenId: int -> tokenId

/// This function maps production indexes returned in syntax errors to strings representing the non terminal that would be produced by that production
val prodIdxToNonTerminal: int -> nonTerminalId

/// This function gets the name of a token as a string
val token_to_string: token -> string
val unit : (FSharp.Text.Lexing.LexBuffer<'cty> -> token) -> FSharp.Text.Lexing.LexBuffer<'cty> -> ( Unit ) 
