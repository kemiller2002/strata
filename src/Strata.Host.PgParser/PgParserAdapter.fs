namespace Strata.Host.PgParser

open System
open PgSqlParser
open Strata.Semantic.Identity
open Strata.Analysis.StatementReferences
open Strata.Host.PgParser.DialectParser

/// PostgreSQL parser adapter over pgsqlparser (libpg_query).
///
/// Authority for: translating a libpg_query parse tree into Strata's
/// dialect-neutral extraction result.
///
/// Everything protobuf-shaped stops here. The walk below is the only code in
/// Strata that knows what a `RangeVar` or `ColumnRef` is.
///
/// The role assignment in `collectRelations` is the correctness-critical part:
/// it is what lets Tier 2 tell a CTE name from a table reference, which
/// EV-STRATA-2026-B9C4 showed a naive walk cannot do.
module PgParserAdapter =

    /// Quoting is not recoverable from the parse tree: libpg_query normalises
    /// unquoted identifiers to lower case and reports the text only. An
    /// identifier whose text differs from its lower-cased form must have been
    /// quoted to survive; otherwise Strata cannot tell, and assumes unquoted.
    ///
    /// This is a known approximation, not a silent one: it is recorded as a
    /// limitation of the adapter and is why `Identifier` keeps `WasQuoted` as
    /// data rather than deriving it downstream.
    let private identifierOf (text: string) =
        if text <> text.ToLowerInvariant() then Identifier.quoted text
        else Identifier.unquoted text

    let private qualifiedNameOf (schema: string) (name: string) =
        if String.IsNullOrEmpty schema then
            QualifiedName.unqualified (identifierOf name)
        else
            QualifiedName.qualified (identifierOf schema) (identifierOf name)

    /// Generic descent over the protobuf message tree.
    let rec private descend (msg: Google.Protobuf.IMessage) (visit: Google.Protobuf.IMessage -> unit) =
        visit msg

        for field in msg.Descriptor.Fields.InFieldNumberOrder() do
            match field.IsRepeated, field.FieldType with
            | true, Google.Protobuf.Reflection.FieldType.Message ->
                match field.Accessor.GetValue msg with
                | :? System.Collections.IEnumerable as items ->
                    for item in items do
                        match item with
                        | :? Google.Protobuf.IMessage as m -> descend m visit
                        | _ -> ()
                | _ -> ()
            | false, Google.Protobuf.Reflection.FieldType.Message ->
                match field.Accessor.GetValue msg with
                | :? Google.Protobuf.IMessage as m when not (isNull (box m)) -> descend m visit
                | _ -> ()
            | _ -> ()

    /// Names bound by WITH clauses anywhere in the statement.
    ///
    /// Collected separately and up front, because a CTE defined in a WITH clause
    /// shadows a table of the same name in the statement body, and the body's
    /// RangeVar carries nothing to distinguish it.
    let private cteNames (stmt: Google.Protobuf.IMessage) =
        let names = ResizeArray<string>()

        descend stmt (fun m ->
            match m with
            | :? CommonTableExpr as cte when not (String.IsNullOrEmpty cte.Ctename) -> names.Add cte.Ctename
            | _ -> ())

        set names

    /// Relations mentioned, each tagged with the role that decides its meaning.
    let private collectRelations (stmt: Google.Protobuf.IMessage) =
        let ctes = cteNames stmt
        let mentions = ResizeArray<RelationMention>()

        // CTE definition sites, so Tier 2 can build bindings from them.
        for name in ctes do
            mentions.Add
                { Name = QualifiedName.unqualified (identifierOf name)
                  Role = CommonTableExpressionDefinition
                  Alias = None
                  QueryLevel = 0 }

        descend stmt (fun m ->
            match m with
            | :? RangeVar as rv when not (String.IsNullOrEmpty rv.Relname) ->
                let alias =
                    if isNull (box rv.Alias) || String.IsNullOrEmpty rv.Alias.Aliasname then None
                    else Some(identifierOf rv.Alias.Aliasname)

                // relpersistence 't' marks a temporary relation. Distinguishing it
                // here keeps a temp table from being treated as a managed object.
                let isTemporary = rv.Relpersistence = "t"

                let role =
                    if isTemporary then TemporaryRelationDefinition
                    elif String.IsNullOrEmpty rv.Schemaname && ctes.Contains rv.Relname then
                        // A bare name matching a WITH binding. Reported as a
                        // reference; Tier 2's scope chain resolves it to the CTE
                        // rather than to a table. This is the RK-001 case.
                        RelationReference
                    else RelationReference

                mentions.Add
                    { Name = qualifiedNameOf rv.Schemaname rv.Relname
                      Role = role
                      Alias = alias
                      QueryLevel = 0 }
            | _ -> ())

        List.ofSeq mentions

    let private collectColumns (stmt: Google.Protobuf.IMessage) =
        let mentions = ResizeArray<ColumnMention>()

        descend stmt (fun m ->
            match m with
            | :? ColumnRef as cr ->
                let parts =
                    cr.Fields
                    |> Seq.map (fun node ->
                        if not (isNull (box node.String)) && not (String.IsNullOrEmpty node.String.Sval) then
                            Choice1Of2 node.String.Sval
                        else
                            Choice2Of2())
                    |> List.ofSeq

                let names = parts |> List.choose (function Choice1Of2 s -> Some s | Choice2Of2 _ -> None)
                let hasStar = parts |> List.exists (function Choice2Of2 _ -> true | Choice1Of2 _ -> false)

                let mention =
                    match names, hasStar with
                    | [], true ->
                        { Qualifier = None; Column = None; IsWildcard = true; QueryLevel = 0 }
                    | [ qualifier ], true ->
                        // `t.*` — qualified wildcard.
                        { Qualifier = Some(identifierOf qualifier)
                          Column = None
                          IsWildcard = true
                          QueryLevel = 0 }
                    | [ column ], false ->
                        { Qualifier = None
                          Column = Some(identifierOf column)
                          IsWildcard = false
                          QueryLevel = 0 }
                    | qualifier :: rest, false when not rest.IsEmpty ->
                        { Qualifier = Some(identifierOf qualifier)
                          Column = Some(identifierOf (List.last rest))
                          IsWildcard = false
                          QueryLevel = 0 }
                    | _ ->
                        { Qualifier = None; Column = None; IsWildcard = hasStar; QueryLevel = 0 }

                mentions.Add mention
            | _ -> ())

        List.ofSeq mentions

    let private shapeOf (stmt: Node) =
        match stmt.NodeCase with
        | Node.NodeOneofCase.SelectStmt -> SelectShape
        | Node.NodeOneofCase.InsertStmt -> InsertShape
        | Node.NodeOneofCase.UpdateStmt -> UpdateShape
        | Node.NodeOneofCase.DeleteStmt -> DeleteShape
        | Node.NodeOneofCase.CreateStmt -> DdlShape "CREATE TABLE"
        | Node.NodeOneofCase.AlterTableStmt -> DdlShape "ALTER TABLE"
        | Node.NodeOneofCase.DropStmt -> DdlShape "DROP"
        | Node.NodeOneofCase.IndexStmt -> DdlShape "CREATE INDEX"
        | Node.NodeOneofCase.ViewStmt -> DdlShape "CREATE VIEW"
        | Node.NodeOneofCase.CreateFunctionStmt -> DdlShape "CREATE FUNCTION"
        | Node.NodeOneofCase.CreateTrigStmt -> DdlShape "CREATE TRIGGER"
        | Node.NodeOneofCase.TruncateStmt -> UtilityShape "TRUNCATE"
        | Node.NodeOneofCase.GrantStmt -> UtilityShape "GRANT"
        | Node.NodeOneofCase.TransactionStmt -> UtilityShape "TRANSACTION"
        | other -> UnsupportedShape(string other)

    /// Does this statement execute dynamically constructed SQL?
    let private containsDynamicSql (stmt: Google.Protobuf.IMessage) =
        let mutable found = false

        descend stmt (fun m ->
            match m with
            | :? ExecuteStmt -> found <- true
            | _ -> ())

        found

    let private errorOf (e: PgSqlParser.Error) =
        { Message = e.Message
          CursorPosition = e.CursorPos
          Context = if String.IsNullOrEmpty e.Context then None else Some e.Context }

    /// Extract one statement into the dialect-neutral shape.
    let extractStatement (stmt: Node) : StatementExtraction =
        { Shape = shapeOf stmt
          Relations = collectRelations stmt
          Columns = collectColumns stmt
          UnmodelledConstructs =
            match shapeOf stmt with
            | UnsupportedShape detail -> [ detail ]
            | SelectShape | InsertShape | UpdateShape | DeleteShape
            | DdlShape _ | UtilityShape _ -> []
          ContainsDynamicSql = containsDynamicSql stmt }

    /// The adapter.
    type PostgresParser() =

        interface IDialectParser with

            member _.Identity =
                { AdapterName = "pgsqlparser 1.0.0"
                  DialectVersion = Parser.PgVersion
                  DialectMajor =
                    match Int32.TryParse Parser.PgMajorVersion with
                    | true, major -> major
                    | false, _ -> 0 }

            member _.ParseScript(sql: string) =
                let result = Parser.Parse(sql, ParserOptions())

                if not result.IsSuccess then
                    // The whole script failed to parse. One failure covering the
                    // whole input is honest; inventing per-statement locations
                    // Strata does not have would not be.
                    [ Failed({ Offset = 0; Length = sql.Length }, errorOf result.Error) ]
                else
                    result.Value.Stmts
                    |> Seq.map (fun raw ->
                        let location =
                            { Offset = raw.StmtLocation
                              Length = raw.StmtLen }

                        Parsed(location, extractStatement raw.Stmt))
                    |> List.ofSeq

            member _.ParseRoutineBody(body: string) =
                let result = Parser.ParsePlpgsql body
                if result.IsSuccess then Ok() else Microsoft.FSharp.Core.Error(errorOf result.Error)

            member _.Fingerprint(sql: string) =
                let result = Parser.Fingerprint(sql, ParserOptions())
                if result.IsSuccess then Ok(string result.Value) else Microsoft.FSharp.Core.Error(errorOf result.Error)
