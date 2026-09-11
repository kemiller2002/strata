namespace Strata.Host.PgParser

open System
open PgSqlParser
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Analysis.StatementReferences
open Strata.Analysis.DialectPort

/// PostgreSQL parser adapter over pgsqlparser (libpg_query).
///
/// Authority for: translating a libpg_query parse tree into Strata's
/// dialect-neutral extraction result.
///
/// Everything protobuf-shaped stops here. The walk below is the only code in
/// Strata that knows what a `RangeVar` or `ColumnRef` is.
///
/// The role assignment in `relationsOf` is the correctness-critical part:
/// it is what lets Tier 2 tell a CTE name from a table reference, which
/// EV-STRATA-2026-B9C4 showed a naive walk cannot do.
///
/// ## One traversal, not six
///
/// The tree is walked exactly once. Reflective descent over the protobuf
/// message graph costs about 2.6 us per node and is the dominant cost of
/// extraction -- far above parsing itself, which is roughly 7% of the total.
/// An earlier version of this module ran six independent `descend` passes
/// (CTE names, relations, columns, join predicates, unmodelled shapes,
/// dynamic SQL), paying that reflective cost six times over for six
/// accumulators that are all functions of the same node stream.
///
/// `foldTree` threads one immutable `Gathered` state through a single visit.
/// Everything that is a pure field read on the statement root -- shape, ALTER
/// actions, INSERT/UPDATE target columns, DROP targets, WHERE presence --
/// stays outside the walk, because none of it needs a traversal at all.
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

    /// A relation mention as it comes off the tree, before the CTE set is known.
    ///
    /// Role assignment needs the complete set of WITH bindings, which is only
    /// final once the whole statement has been walked. Keeping the raw shape
    /// here is what lets the walk run once: gather first, decide afterwards.
    type private RawRelation =
        { Schema: string
          Relname: string
          Alias: Identifier option
          IsTemporary: bool }

    /// Everything the single traversal accumulates.
    ///
    /// Lists are built in reverse and flipped once at the end, so each matched
    /// node costs O(1) rather than O(n). Nodes that match nothing -- the large
    /// majority -- return the state unchanged and allocate nothing.
    type private Gathered =
        { Ctes: string list
          Relations: RawRelation list
          Columns: ColumnMention list
          JoinPredicates: JoinPredicate list
          UnmodelledShapes: string list
          ContainsDynamicSql: bool }

    module private Gathered =

        let empty =
            { Ctes = []
              Relations = []
              Columns = []
              JoinPredicates = []
              UnmodelledShapes = []
              ContainsDynamicSql = false }

    /// Generic fold over the protobuf message tree.
    ///
    /// `visit` is pure: it takes a state and a node and returns the next state.
    /// The single mutable local is the accumulator threaded across the child
    /// loop; making it a `Seq.fold` instead would allocate an enumerator per
    /// field on the hottest path in the system for no behavioural gain.
    let rec private foldTree
        (visit: 'State -> Google.Protobuf.IMessage -> 'State)
        (state: 'State)
        (msg: Google.Protobuf.IMessage)
        : 'State =
        let mutable acc = visit state msg

        for field in msg.Descriptor.Fields.InFieldNumberOrder() do
            match field.IsRepeated, field.FieldType with
            | true, Google.Protobuf.Reflection.FieldType.Message ->
                match field.Accessor.GetValue msg with
                | :? System.Collections.IEnumerable as items ->
                    for item in items do
                        match item with
                        | :? Google.Protobuf.IMessage as m -> acc <- foldTree visit acc m
                        | _ -> ()
                | _ -> ()
            | false, Google.Protobuf.Reflection.FieldType.Message ->
                match field.Accessor.GetValue msg with
                | :? Google.Protobuf.IMessage as m when not (isNull (box m)) -> acc <- foldTree visit acc m
                | _ -> ()
            | _ -> ()

        acc

    /// Relations named by a DROP statement.
    ///
    /// `DropStmt` lists its targets in `Objects` as name lists, not as
    /// `RangeVar` nodes, so the generic relation walk never sees them. Without
    /// this a `DROP TABLE` reaches the gate with no resolvable target and is
    /// classified as unmodelled — the single most destructive statement in SQL,
    /// invisible.
    let private dropTargets (stmt: Node) =
        if stmt.NodeCase <> Node.NodeOneofCase.DropStmt then []
        else
            stmt.DropStmt.Objects
            |> Seq.choose (fun node ->
                if isNull (box node.List) then None
                else
                    let parts =
                        node.List.Items
                        |> Seq.choose (fun item ->
                            if not (isNull (box item.String)) && not (String.IsNullOrEmpty item.String.Sval) then
                                Some item.String.Sval
                            else
                                None)
                        |> List.ofSeq

                    match parts with
                    | [ name ] -> Some(QualifiedName.unqualified (identifierOf name))
                    | [ schema; name ] -> Some(qualifiedNameOf schema name)
                    | _ -> None)
            |> List.ofSeq

    /// A `ColumnRef`'s name parts, for join-predicate matching.
    let private columnParts (node: Node) =
        if isNull (box node) || isNull (box node.ColumnRef) then None
        else
            let names =
                node.ColumnRef.Fields
                |> Seq.choose (fun f ->
                    if not (isNull (box f.String)) && not (String.IsNullOrEmpty f.String.Sval) then
                        Some f.String.Sval
                    else
                        None)
                |> List.ofSeq

            match names with
            | [ column ] -> Some(None, identifierOf column)
            | qualifier :: rest when not rest.IsEmpty ->
                Some(Some(identifierOf qualifier), identifierOf (List.last rest))
            | _ -> None

    let private isColumnRef (node: Node) =
        not (isNull (box node)) && not (isNull (box node.ColumnRef))

    /// The operator symbol an `A_Expr` names.
    ///
    /// libpg_query reports the operator as a NAME PATH, not a string: `=` is
    /// `["="]`, but `OPERATOR(pg_catalog.=)` is `["pg_catalog"; "="]`. The
    /// symbol is the LAST element; anything before it is the schema.
    ///
    /// Reading the first element instead is what made a schema-qualified
    /// equality report itself as an unmodelled "predicate with operator
    /// 'pg_catalog'" while the join path — which scanned every element for
    /// `=` — correctly recorded it as a join. The two paths disagreed about
    /// the same node, so they now share this one reader.
    let private operatorSymbol (expr: A_Expr) =
        expr.Name
        |> Seq.choose (fun n ->
            if not (isNull (box n.String)) && not (String.IsNullOrEmpty n.String.Sval) then
                Some n.String.Sval
            else
                None)
        |> Seq.tryLast

    let private columnMentionOf (cr: ColumnRef) =
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

    /// Join predicates contributed by one `A_Expr`.
    ///
    /// Equality predicates between two column references. This deliberately
    /// catches both `JOIN ... ON a.x = b.y` and `WHERE a.x = b.y`, since the
    /// latter is a join in older SQL style.
    ///
    /// A comparison against a literal or parameter is NOT a join predicate and
    /// is skipped: only column-to-column equality is relationship evidence.
    let private joinPredicateOf (expr: A_Expr) =
        if expr.Kind <> A_Expr_Kind.AexprOp then None
        else
            // A schema-qualified operator such as `OPERATOR(pg_catalog.=)` is
            // still equality: the symbol is the last name element.
            if operatorSymbol expr <> Some "=" then None
            else
                match columnParts expr.Lexpr, columnParts expr.Rexpr with
                | Some (leftQualifier, leftColumn), Some (rightQualifier, rightColumn) ->
                    Some
                        { LeftQualifier = leftQualifier
                          LeftColumn = leftColumn
                          RightQualifier = rightQualifier
                          RightColumn = rightColumn
                          QueryLevel = 0 }
                | _ -> None

    /// Predicates that relate two columns but which Strata does not model as
    /// join evidence.
    ///
    /// Join detection above is equality-only. Without this, a corpus written
    /// with range joins or `IN (SELECT ...)` yields FEWER relationships with no
    /// indication anything was missed — making "no relationship found"
    /// indistinguishable from "that shape is not analysed", which is exactly
    /// the collapse ER-008 forbids.
    ///
    /// These are reported as unmodelled constructs so they become explicit
    /// analysis gaps rather than silent omissions.
    let private unmodelledShapeOf (expr: A_Expr) =
        let operatorName = operatorSymbol expr

        match expr.Kind with
        | A_Expr_Kind.AexprOp ->
            // A non-equality operator relating two columns is a range
            // join. Real relationship evidence Strata does not model.
            if isColumnRef expr.Lexpr && isColumnRef expr.Rexpr then
                match operatorName with
                | Some "=" -> None
                | Some op -> Some(sprintf "column-to-column predicate with operator '%s' is not modelled as join evidence" op)
                | None -> Some "column-to-column predicate with an unnamed operator is not modelled as join evidence"
            else None
        | A_Expr_Kind.AexprBetween
        | A_Expr_Kind.AexprNotBetween
        | A_Expr_Kind.AexprBetweenSym
        | A_Expr_Kind.AexprNotBetweenSym ->
            if isColumnRef expr.Lexpr then Some "BETWEEN predicate is not modelled as join evidence"
            else None
        | A_Expr_Kind.AexprIn ->
            if isColumnRef expr.Lexpr then Some "IN predicate is not modelled as join evidence"
            else None
        | _ -> None

    /// The single visit.
    ///
    /// Every accumulator that used to own a traversal is a branch here. The
    /// `A_Expr` case feeds two of them, which is why they share one type test
    /// rather than two walks.
    let private gather (acc: Gathered) (m: Google.Protobuf.IMessage) : Gathered =
        match m with
        | :? CommonTableExpr as cte when not (String.IsNullOrEmpty cte.Ctename) ->
            { acc with Ctes = cte.Ctename :: acc.Ctes }

        | :? RangeVar as rv when not (String.IsNullOrEmpty rv.Relname) ->
            let alias =
                if isNull (box rv.Alias) || String.IsNullOrEmpty rv.Alias.Aliasname then None
                else Some(identifierOf rv.Alias.Aliasname)

            // relpersistence 't' marks a temporary relation. Distinguishing it
            // here keeps a temp table from being treated as a managed object.
            let raw =
                { Schema = rv.Schemaname
                  Relname = rv.Relname
                  Alias = alias
                  IsTemporary = rv.Relpersistence = "t" }

            { acc with Relations = raw :: acc.Relations }

        | :? ColumnRef as cr ->
            { acc with Columns = columnMentionOf cr :: acc.Columns }

        | :? A_Expr as expr ->
            let withJoin =
                match joinPredicateOf expr with
                | Some predicate -> { acc with JoinPredicates = predicate :: acc.JoinPredicates }
                | None -> acc

            match unmodelledShapeOf expr with
            | Some shape -> { withJoin with UnmodelledShapes = shape :: withJoin.UnmodelledShapes }
            | None -> withJoin

        | :? SubLink as sublink ->
            // `x IN (SELECT ...)`, `EXISTS (SELECT ...)` and friends relate
            // the outer query to an inner one. Strata extracts the inner
            // relations as references but does not derive a relationship.
            let shape =
                match sublink.SubLinkType with
                | SubLinkType.AnySublink -> Some "IN (SELECT ...) subquery is not modelled as join evidence"
                | SubLinkType.ExistsSublink -> Some "EXISTS (SELECT ...) subquery is not modelled as join evidence"
                | SubLinkType.AllSublink -> Some "ALL (SELECT ...) subquery is not modelled as join evidence"
                | _ -> None

            match shape with
            | Some s -> { acc with UnmodelledShapes = s :: acc.UnmodelledShapes }
            | None -> acc

        | :? ExecuteStmt -> { acc with ContainsDynamicSql = true }

        | _ -> acc

    /// Relations mentioned, each tagged with the role that decides its meaning.
    ///
    /// Roles are assigned here rather than during the walk because a CTE
    /// defined in a WITH clause shadows a table of the same name in the
    /// statement body, and the body's `RangeVar` carries nothing to
    /// distinguish it — so the decision needs the complete set of WITH
    /// bindings, which only exists once the walk is done.
    let private relationsOf (stmt: Node) (gathered: Gathered) =
        let ctes = Set.ofList gathered.Ctes

        // CTE definition sites, so Tier 2 can build bindings from them.
        let definitions =
            ctes
            |> Seq.map (fun name ->
                { Name = QualifiedName.unqualified (identifierOf name)
                  Role = CommonTableExpressionDefinition
                  Alias = None
                  QueryLevel = 0 })
            |> List.ofSeq

        let references =
            gathered.Relations
            |> List.rev
            |> List.map (fun raw ->
                let role =
                    if raw.IsTemporary then TemporaryRelationDefinition
                    elif String.IsNullOrEmpty raw.Schema && ctes.Contains raw.Relname then
                        // A bare name matching a WITH binding. Reported as a
                        // reference; Tier 2's scope chain resolves it to the CTE
                        // rather than to a table. This is the RK-001 case.
                        RelationReference
                    else RelationReference

                { Name = qualifiedNameOf raw.Schema raw.Relname
                  Role = role
                  Alias = raw.Alias
                  QueryLevel = 0 })

        // DROP targets are not RangeVar nodes and must be added explicitly.
        let drops =
            dropTargets stmt
            |> List.map (fun name ->
                { Name = name
                  Role = RelationReference
                  Alias = None
                  QueryLevel = 0 })

        definitions @ references @ drops

    /// Columns an INSERT or UPDATE targets.
    ///
    /// These live in `ResTarget.Name`, NOT in a `ColumnRef`, so the generic
    /// column walk never sees them. Without this, `UPDATE t SET c = ...`
    /// contributes no dependency on `c` at all — which would make a column
    /// impact report omit every writer of the column, understating the blast
    /// radius of a drop in exactly the direction that causes damage.
    let private collectTargetColumns (stmt: Node) =
        let targets =
            match stmt.NodeCase with
            | Node.NodeOneofCase.UpdateStmt -> stmt.UpdateStmt.TargetList |> Seq.toList
            | Node.NodeOneofCase.InsertStmt -> stmt.InsertStmt.Cols |> Seq.toList
            | _ -> []

        targets
        |> List.choose (fun node ->
            if isNull (box node.ResTarget) || String.IsNullOrEmpty node.ResTarget.Name then None
            else
                Some
                    { Qualifier = None
                      Column = Some(identifierOf node.ResTarget.Name)
                      IsWildcard = false
                      QueryLevel = 0 })

    /// Actions inside an ALTER TABLE.
    ///
    /// libpg_query models these as `AlterTableCmd` nodes carrying a `Subtype`
    /// enum and a `Name`. Reading the enum is what lets the gate distinguish an
    /// additive change from a destructive one; the statement text cannot be
    /// trusted for that.
    let private collectAlterActions (stmt: Node) =
        if stmt.NodeCase <> Node.NodeOneofCase.AlterTableStmt then []
        else
            stmt.AlterTableStmt.Cmds
            |> Seq.choose (fun node ->
                if isNull (box node.AlterTableCmd) then None
                else
                    let cmd = node.AlterTableCmd

                    let kind =
                        match cmd.Subtype with
                        | AlterTableType.AtAddColumn -> "add-column"
                        | AlterTableType.AtDropColumn -> "drop-column"
                        | AlterTableType.AtAlterColumnType -> "alter-column-type"
                        | AlterTableType.AtAddConstraint -> "add-constraint"
                        | AlterTableType.AtDropConstraint -> "drop-constraint"
                        | AlterTableType.AtSetNotNull -> "set-not-null"
                        | AlterTableType.AtDropNotNull -> "drop-not-null"
                        | AlterTableType.AtColumnDefault -> "set-default"
                        | other -> "other:" + string other

                    // `cmd.Name` carries the column for DROP COLUMN and
                    // ALTER COLUMN TYPE, but ADD COLUMN puts it in the ColumnDef
                    // instead. Reading only one of the two silently loses the
                    // additive case, which is the case a gate must get right to
                    // avoid blocking safe migrations.
                    let nameFromDef =
                        if isNull (box cmd.Def) || isNull (box cmd.Def.ColumnDef) then None
                        elif String.IsNullOrEmpty cmd.Def.ColumnDef.Colname then None
                        else Some(identifierOf cmd.Def.ColumnDef.Colname)

                    Some
                        { Kind = kind
                          Name =
                            if not (String.IsNullOrEmpty cmd.Name) then Some(identifierOf cmd.Name)
                            else nameFromDef })
            |> List.ofSeq

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

    /// Does this statement carry a WHERE predicate?
    ///
    /// Read from the statement node's own whereClause rather than by searching
    /// the tree: a WHERE inside a subquery does not bound the outer write, and
    /// treating it as though it did would understate the blast radius of an
    /// unbounded UPDATE or DELETE — the exact §10 failure.
    let private hasWhereClause (stmt: Node) =
        match stmt.NodeCase with
        | Node.NodeOneofCase.SelectStmt -> not (isNull (box stmt.SelectStmt.WhereClause))
        | Node.NodeOneofCase.UpdateStmt -> not (isNull (box stmt.UpdateStmt.WhereClause))
        | Node.NodeOneofCase.DeleteStmt -> not (isNull (box stmt.DeleteStmt.WhereClause))
        | _ -> false

    // ---- desired state ------------------------------------------------------
    //
    // Everything below reads what a statement DECLARES, for PR-025. The walk
    // above reads what a statement REFERENCES. They share a grammar and
    // nothing else: a `CREATE TABLE` mentions its own columns nowhere a
    // `ColumnRef` walk can see them, which is why extracting five columns from
    // a five-column table previously yielded one.

    /// PostgreSQL's internal type names, mapped to the spelling the catalog
    /// reports.
    ///
    /// The parser canonicalises `bigint` to `int8`; `format_type()`, which
    /// introspection reads, renders the same type as `bigint`. They are the
    /// same type spelled two ways, and a diff comparing the two spellings
    /// reports every column of every table as changed, forever. Measured
    /// against a live database: 5 of 7 columns differed on type alone while
    /// every structural property matched.
    ///
    /// The catalog's spelling wins because it is the one a human sees in
    /// `\d`, and because the declared side is the one Strata controls.
    let private canonicalTypeNames =
        dict [ "int8", "bigint"
               "int4", "integer"
               "int2", "smallint"
               "float8", "double precision"
               "float4", "real"
               "bool", "boolean"
               "varchar", "character varying"
               "bpchar", "character"
               "timestamptz", "timestamp with time zone"
               "timestamp", "timestamp without time zone"
               "timetz", "time with time zone"
               "time", "time without time zone" ]

    /// Integer type modifiers, in order: the `(12,2)` of `numeric(12,2)`.
    ///
    /// An earlier version dropped these on the stated grounds that "the catalog
    /// side does not carry them either". That was simply wrong — introspection
    /// reports `numeric(12,2)` — and the round-trip against a live database is
    /// what exposed it.
    let private typeModifiers (typeName: TypeName) =
        if isNull (box typeName.Typmods) then []
        else
            typeName.Typmods
            |> Seq.choose (fun node ->
                if isNull (box node.AConst) || isNull (box node.AConst.Ival) then None
                else Some(string node.AConst.Ival.Ival))
            |> List.ofSeq

    /// A type name as written, flattened from libpg_query's name path and
    /// rendered the way the catalog renders it.
    let private typeNameOf (typeName: TypeName) =
        if isNull (box typeName) then QualifiedName.unqualified (identifierOf "unknown")
        else
            let parts =
                typeName.Names
                |> Seq.choose (fun n ->
                    if not (isNull (box n.String)) && not (String.IsNullOrEmpty n.String.Sval) then
                        Some n.String.Sval
                    else
                        None)
                |> List.ofSeq

            let canonical (name: string) =
                match canonicalTypeNames.TryGetValue name with
                | true, mapped -> mapped
                | false, _ -> name

            let withModifiers (name: string) =
                match typeModifiers typeName with
                | [] -> name
                | modifiers ->
                    // `timestamp(3) with time zone` — the precision sits inside
                    // the phrase, not after it, so a naive append is wrong for
                    // exactly the types whose canonical spelling is a phrase.
                    let rendered = String.concat "," modifiers

                    if name.Contains " with time zone" then
                        name.Replace(" with time zone", sprintf "(%s) with time zone" rendered)
                    elif name.Contains " without time zone" then
                        name.Replace(" without time zone", sprintf "(%s) without time zone" rendered)
                    else
                        sprintf "%s(%s)" name rendered

            match parts with
            | [ "pg_catalog"; name ] -> QualifiedName.unqualified (identifierOf (withModifiers (canonical name)))
            | [ name ] -> QualifiedName.unqualified (identifierOf (withModifiers (canonical name)))
            | [ schema; name ] -> qualifiedNameOf schema (withModifiers name)
            | _ -> QualifiedName.unqualified (identifierOf "unknown")

    let private constraintsOf (col: ColumnDef) =
        if isNull (box col.Constraints) then []
        else
            col.Constraints
            |> Seq.choose (fun n -> if isNull (box n.Constraint) then None else Some n.Constraint)
            |> List.ofSeq

    /// One column of a table definition.
    ///
    /// Nullability is NOT NULL-or-absent in the grammar, so the default is
    /// nullable — matching PostgreSQL rather than guessing. A PRIMARY KEY
    /// written inline implies NOT NULL, which the catalog reports and a file
    /// therefore must too, or every primary key column would diff.
    /// The serial pseudo-types, and what PostgreSQL actually creates.
    ///
    /// `serial` is not a type. `id bigserial` becomes
    /// `id bigint NOT NULL DEFAULT nextval(...)` plus a sequence, so the
    /// catalog reports `bigint` WITH a default while the file says `bigserial`
    /// with none. Read literally that is two differences on a column nobody
    /// changed — and `serial` is common enough that a diff would report
    /// permanent churn on most real schemas.
    ///
    /// Found by round-tripping a created table back through the diff, the same
    /// way the int8/bigint spelling mismatch was.
    let private serialTypes =
        dict [ "smallserial", "smallint"
               "serial2", "smallint"
               "serial", "integer"
               "serial4", "integer"
               "bigserial", "bigint"
               "serial8", "bigint" ]

    let private columnOf (position: int) (col: ColumnDef) =
        let constraints = constraintsOf col

        let hasKind kind =
            constraints |> List.exists (fun c -> c.Contype = kind)

        let declaredType = typeNameOf col.TypeName

        let serial =
            match declaredType.Schema with
            | Some _ -> None
            | None ->
                match serialTypes.TryGetValue(Identifier.folded declaredType.Name) with
                | true, underlying -> Some underlying
                | false, _ -> None

        let isNotNull =
            hasKind ConstrType.ConstrNotnull
            || hasKind ConstrType.ConstrPrimary
            // A serial column is NOT NULL whether or not the author wrote it.
            || serial.IsSome

        { Name = identifierOf col.Colname
          Type =
            { TypeName =
                match serial with
                | Some underlying -> QualifiedName.unqualified (identifierOf underlying)
                | None -> declaredType
              IsNullable = not isNotNull }
          Position = position
          // A serial column always has a nextval default, which the catalog
          // reports and the file does not write.
          HasDefault = hasKind ConstrType.ConstrDefault || serial.IsSome
          IsGenerated = hasKind ConstrType.ConstrGenerated
          IsIdentity = hasKind ConstrType.ConstrIdentity }

    let private keyNames (keys: Google.Protobuf.Collections.RepeatedField<Node>) =
        if isNull (box keys) then []
        else
            keys
            |> Seq.choose (fun n ->
                if not (isNull (box n.String)) && not (String.IsNullOrEmpty n.String.Sval) then
                    Some(identifierOf n.String.Sval)
                else
                    None)
            |> List.ofSeq

    /// Read a CREATE TABLE into the semantic model.
    ///
    /// Table-level and column-inline constraints are both collected: the same
    /// primary key can be written either way and the catalog cannot tell which
    /// the author chose, so a loader that read only one form would diff a table
    /// against itself.
    let private tableOf (stmt: CreateStmt) =
        let elements =
            if isNull (box stmt.TableElts) then []
            else stmt.TableElts |> List.ofSeq

        let columnDefs =
            elements
            |> List.choose (fun n -> if isNull (box n.ColumnDef) then None else Some n.ColumnDef)

        let columns = columnDefs |> List.mapi (fun i c -> columnOf (i + 1) c)

        // Constraints written at table level.
        let tableConstraints =
            elements
            |> List.choose (fun n -> if isNull (box n.Constraint) then None else Some n.Constraint)

        // Constraints written inline on a column, paired with that column.
        let inlineConstraints =
            columnDefs
            |> List.collect (fun c -> constraintsOf c |> List.map (fun con -> identifierOf c.Colname, con))

        let constraintName (c: Constraint) fallback =
            if isNull (box c) || String.IsNullOrEmpty c.Conname then identifierOf fallback
            else identifierOf c.Conname

        // PrimaryKey and UniqueConstraint are structurally identical, so these
        // are annotated: without it F# infers the later-declared type and the
        // table would carry its primary key in the wrong field.
        let primaryKey: PrimaryKey option =
            let fromTable =
                tableConstraints
                |> List.tryFind (fun c -> c.Contype = ConstrType.ConstrPrimary)
                |> Option.map (fun c ->
                    { PrimaryKey.ConstraintName = constraintName c "primary_key"
                      Columns = keyNames c.Keys })

            match fromTable with
            | Some pk -> Some pk
            | None ->
                inlineConstraints
                |> List.tryFind (fun (_, c) -> c.Contype = ConstrType.ConstrPrimary)
                |> Option.map (fun (column, c) ->
                    { PrimaryKey.ConstraintName = constraintName c "primary_key"
                      Columns = [ column ] })

        let uniques: UniqueConstraint list =
            (tableConstraints
             |> List.filter (fun c -> c.Contype = ConstrType.ConstrUnique)
             |> List.map (fun c ->
                 { ConstraintName = constraintName c "unique"
                   Columns = keyNames c.Keys }))
            @ (inlineConstraints
               |> List.filter (fun (_, c) -> c.Contype = ConstrType.ConstrUnique)
               |> List.map (fun (column, c) ->
                   { ConstraintName = constraintName c "unique"
                     Columns = [ column ] }))

        let checks =
            (tableConstraints |> List.filter (fun c -> c.Contype = ConstrType.ConstrCheck)
             |> List.map (fun c -> constraintName c "check", c))
            @ (inlineConstraints |> List.filter (fun (_, c) -> c.Contype = ConstrType.ConstrCheck)
               |> List.map (fun (_, c) -> constraintName c "check", c))
            |> List.map (fun (name, _) ->
                // The predicate TEXT is not recoverable from the parse tree
                // without deparsing, and the catalog reports it already
                // normalised. Carrying the name alone keeps a check constraint
                // visible as an object without inventing an expression that
                // would diff against the server's own rendering every time.
                { ConstraintName = name; Expression = "" })

        let foreignKeys =
            (tableConstraints
             |> List.filter (fun c -> c.Contype = ConstrType.ConstrForeign)
             |> List.map (fun c -> None, c))
            @ (inlineConstraints
               |> List.filter (fun (_, c) -> c.Contype = ConstrType.ConstrForeign)
               |> List.map (fun (column, c) -> Some column, c))
            |> List.map (fun (inlineColumn, c) ->
                let columns =
                    match inlineColumn with
                    | Some column -> [ column ]
                    | None -> keyNames c.FkAttrs

                { ConstraintName = constraintName c "foreign_key"
                  Columns = columns
                  ReferencedTable =
                    if isNull (box c.Pktable) then QualifiedName.unqualified (identifierOf "unknown")
                    else qualifiedNameOf c.Pktable.Schemaname c.Pktable.Relname
                  ReferencedColumns = keyNames c.PkAttrs })

        { Name =
            if isNull (box stmt.Relation) then QualifiedName.unqualified (identifierOf "unknown")
            else qualifiedNameOf stmt.Relation.Schemaname stmt.Relation.Relname
          Columns = columns
          PrimaryKey = primaryKey
          UniqueConstraints = uniques
          CheckConstraints = checks
          ForeignKeys = foreignKeys
          // Indexes are separate statements in PostgreSQL, so a CREATE TABLE
          // declares none. An empty list here means "this statement declared
          // no index", not "this table has none".
          Indexes = []
          // Everything in a project's desired state is by definition managed.
          Scope = Managed }

    /// The body and language of a `CREATE FUNCTION`, when it defines one.
    ///
    /// libpg_query puts both in `Options` as `DefElem` nodes: `as` carries the
    /// body (a List of String, because `AS 'obj_file', 'link_symbol'` is legal
    /// for C functions), `language` carries the language name.
    ///
    /// Only the single-element `as` form yields a body. A two-element one names
    /// an object file and a symbol, which is not SQL and must not be handed to
    /// a SQL parser as though it were.
    let private routineBodyOf (stmt: Node) =
        if stmt.NodeCase <> Node.NodeOneofCase.CreateFunctionStmt then None
        else
            let options =
                if isNull (box stmt.CreateFunctionStmt.Options) then []
                else
                    stmt.CreateFunctionStmt.Options
                    |> Seq.choose (fun n -> if isNull (box n.DefElem) then None else Some n.DefElem)
                    |> List.ofSeq

            let stringsOf (arg: Node) =
                if isNull (box arg) then []
                elif not (isNull (box arg.String)) then [ arg.String.Sval ]
                elif not (isNull (box arg.List)) then
                    arg.List.Items
                    |> Seq.choose (fun i ->
                        if not (isNull (box i.String)) then Some i.String.Sval else None)
                    |> List.ofSeq
                else []

            let valueOf name =
                options
                |> List.tryFind (fun d -> d.Defname = name)
                |> Option.map (fun d -> stringsOf d.Arg)

            match valueOf "as" with
            | Some [ body ] ->
                let language =
                    match valueOf "language" with
                    | Some [ l ] -> l
                    | _ -> "unknown"

                // IN and INOUT parameters are visible as bare names inside the
                // body. OUT parameters are too, so every named parameter is
                // collected regardless of mode.
                let parameters =
                    if isNull (box stmt.CreateFunctionStmt.Parameters) then []
                    else
                        stmt.CreateFunctionStmt.Parameters
                        |> Seq.choose (fun n ->
                            if isNull (box n.FunctionParameter) then None
                            elif String.IsNullOrEmpty n.FunctionParameter.Name then None
                            else Some(identifierOf n.FunctionParameter.Name))
                        |> List.ofSeq

                Some
                    { Language = language
                      Body = body
                      Parameters = parameters }
            | Some _
            | None -> None

    /// A view, as its file declares it.
    ///
    /// `Columns` and `Definition` come back EMPTY, and neither means "none".
    /// A view's column list is a property of the query it wraps and is only
    /// knowable by resolving that query, which the catalog does and a file
    /// cannot; the definition text is not recoverable from the parse tree
    /// without deparsing, and PostgreSQL rewrites what it stores anyway
    /// (`SELECT 1 AS x` comes back schema-qualified and reformatted), so even
    /// a perfect deparse would not compare equal.
    ///
    /// The `View` record cannot express "unknown" for either field. The diff is
    /// therefore required to compare views by PRESENCE only and to report
    /// their definitions as not-compared — never to read these empties as
    /// facts. `DF-STRATA-2026-B1E7` and `ER-008`.
    let private viewOf (name: RangeVar) (isMaterialized: bool) =
        { Name = qualifiedNameOf name.Schemaname name.Relname
          Columns = []
          IsMaterialized = isMaterialized
          Definition = ""
          Scope = Managed }

    /// A routine, as its file declares it.
    ///
    /// Identity is name PLUS argument types: PostgreSQL allows overloads, so
    /// `f(int)` and `f(text)` are different objects and matching on name alone
    /// would make one look like a redefinition of the other.
    let private routineOf (stmt: CreateFunctionStmt) =
        let nameParts =
            if isNull (box stmt.Funcname) then []
            else
                stmt.Funcname
                |> Seq.choose (fun n ->
                    if not (isNull (box n.String)) && not (String.IsNullOrEmpty n.String.Sval) then
                        Some n.String.Sval
                    else
                        None)
                |> List.ofSeq

        let name =
            match nameParts with
            | [ single ] -> QualifiedName.unqualified (identifierOf single)
            | [ schema; single ] -> qualifiedNameOf schema single
            | _ -> QualifiedName.unqualified (identifierOf "unknown")

        let parameters =
            if isNull (box stmt.Parameters) then []
            else
                stmt.Parameters
                |> Seq.choose (fun n -> if isNull (box n.FunctionParameter) then None else Some n.FunctionParameter)
                |> List.ofSeq

        // Only IN and INOUT parameters form the signature; an OUT parameter
        // does not participate in overload resolution, so including it would
        // give the routine an identity PostgreSQL does not recognise.
        let argumentTypes =
            parameters
            |> List.filter (fun p ->
                p.Mode = FunctionParameterMode.FuncParamDefault
                || p.Mode = FunctionParameterMode.FuncParamIn
                || p.Mode = FunctionParameterMode.FuncParamInout
                || p.Mode = FunctionParameterMode.FuncParamVariadic)
            |> List.map (fun p -> QualifiedName.display (typeNameOf p.ArgType))

        let language =
            if isNull (box stmt.Options) then "unknown"
            else
                stmt.Options
                |> Seq.choose (fun n -> if isNull (box n.DefElem) then None else Some n.DefElem)
                |> Seq.tryPick (fun d ->
                    if d.Defname = "language" && not (isNull (box d.Arg)) && not (isNull (box d.Arg.String)) then
                        Some d.Arg.String.Sval
                    else
                        None)
                |> Option.defaultValue "unknown"

        { Name = name
          Kind = if stmt.IsProcedure then Procedure else Function
          ArgumentTypes = argumentTypes
          ReturnType =
            if isNull (box stmt.ReturnType) then None
            else Some(QualifiedName.display (typeNameOf stmt.ReturnType))
          Language = language
          Scope = Managed }

    let private errorOf (e: PgSqlParser.Error) =
        { Message = e.Message
          CursorPosition = e.CursorPos
          Context = if String.IsNullOrEmpty e.Context then None else Some e.Context }

    /// Extract one statement into the dialect-neutral shape.
    ///
    /// One traversal, then assembly from what it gathered plus the root-node
    /// field reads that never needed a traversal.
    let extractStatement (stmt: Node) : StatementExtraction =
        let gathered = foldTree gather Gathered.empty stmt
        let shape = shapeOf stmt

        { Shape = shape
          Relations = relationsOf stmt gathered
          Columns = (gathered.Columns |> List.rev) @ collectTargetColumns stmt
          UnmodelledConstructs =
            (match shape with
             | UnsupportedShape detail -> [ detail ]
             | SelectShape | InsertShape | UpdateShape | DeleteShape
             | DdlShape _ | UtilityShape _ -> [])
            @ (gathered.UnmodelledShapes |> List.rev |> List.distinct)
          ContainsDynamicSql = gathered.ContainsDynamicSql
          JoinPredicates = gathered.JoinPredicates |> List.rev
          AlterActions = collectAlterActions stmt
          HasWherePredicate = hasWhereClause stmt
          RoutineBody = routineBodyOf stmt }

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

            member _.ParseObjectDefinitions(sql: string) =
                let result = Parser.Parse(sql, ParserOptions())

                if not result.IsSuccess then
                    [ DeclarationFailed(errorOf result.Error) ]
                else
                    result.Value.Stmts
                    |> Seq.map (fun raw ->
                        let stmt = raw.Stmt

                        match stmt.NodeCase with
                        | Node.NodeOneofCase.CreateStmt -> Declared(TableObject(tableOf stmt.CreateStmt))

                        | Node.NodeOneofCase.ViewStmt when not (isNull (box stmt.ViewStmt.View)) ->
                            Declared(ViewObject(viewOf stmt.ViewStmt.View false))

                        // CREATE MATERIALIZED VIEW is a CreateTableAsStmt with
                        // an objtype of matview, not a ViewStmt.
                        | Node.NodeOneofCase.CreateTableAsStmt when
                            stmt.CreateTableAsStmt.Objtype = ObjectType.ObjectMatview
                            && not (isNull (box stmt.CreateTableAsStmt.Into))
                            && not (isNull (box stmt.CreateTableAsStmt.Into.Rel)) ->
                            Declared(ViewObject(viewOf stmt.CreateTableAsStmt.Into.Rel true))

                        | Node.NodeOneofCase.CreateFunctionStmt ->
                            Declared(RoutineObject(routineOf stmt.CreateFunctionStmt))
                        // Recognised, modelled nowhere yet. Saying so keeps a
                        // view file from reading as an empty declaration, which
                        // a diff would treat as "nothing to create" (ER-008).
                        | other -> Unmodelled(sprintf "%s is not yet read as a desired-state declaration" (string other)))
                    |> List.ofSeq

            member _.Fingerprint(sql: string) =
                let result = Parser.Fingerprint(sql, ParserOptions())
                if result.IsSuccess then Ok(string result.Value) else Microsoft.FSharp.Core.Error(errorOf result.Error)
