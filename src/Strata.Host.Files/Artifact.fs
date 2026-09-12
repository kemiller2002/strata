namespace Strata.Host.Files

open System
open System.Text.Json
open Strata.Semantic
open Strata.Semantic.Wire
open Strata.Semantic.Identity
open Strata.Semantic.Evidence
open Strata.Semantic.AnalysisScope
open Strata.Semantic.Schema
open Strata.Analysis.DialectPort
open Strata.Application

/// The compiled artifact: desired state, written down.
///
/// Authority for: the on-disk form of `ResolvedDesiredState`, in both
/// directions.
///
/// ## What is in it, and why the whole model
///
/// `DF-STRATA-2026-2F6B` splits a deployment in two. Turning source into
/// desired state depends on the source and on *a* PostgreSQL, so it compiles.
/// Turning desired state into a change list depends on what the TARGET holds at
/// the moment of deployment, so it does not. This file is the boundary.
///
/// The artifact carries the semantic MODEL, not the source text. Carrying the
/// text and re-parsing at deploy time would be a quarter of the code and would
/// give away the thing compiling is for: a parse that happens at deploy time is
/// a parse that can FAIL at deploy time, and then `strata compile` has not
/// promised anything. An artifact that carries the model has already been
/// parsed, already been read by the server, and already been checked.
///
/// It also carries the normalisations — view text, defaults, check and policy
/// expressions, reference rows through the real column types — because those
/// are the half of a comparison that needs a server, and `deploy` must not need
/// one for anything but the target itself.
///
/// ## Why both directions live in one file
///
/// `.sde/architecture/BOUNDARY-PRESERVATION.md` requires wire representations to
/// be hand-written rather than reflected, so a renamed field breaks a test
/// instead of silently changing the format. That applies to reading as much as
/// writing, and a hand-written pair that drifts apart is worse than either half
/// alone: the artifact would be written one way and read another, and the only
/// symptom would be a deployment that is subtly not what was compiled.
///
/// So every type has its `render` and its `read` adjacent, in that order, and
/// `ArtifactTests` round-trips the whole thing. Adding a field means touching
/// two functions that are three lines apart.
///
/// ## Why Tier 4
///
/// Reading JSON needs a parser. Tier 1 may reference only FSharp.Core and Tier 3
/// may take no package reference at all (`DF-STRATA-2026-D3F8`, enforced by
/// `scripts/check-semantic-architecture.sh`), so `System.Text.Json` can only be
/// used here. Rendering could have lived in Tier 3 — `Wire.Json` is Tier 1 — but
/// splitting the pair across two projects is exactly the drift this file is
/// arranged to prevent.
module Artifact =

    /// The format version, written into every artifact and checked on read.
    ///
    /// An artifact is read by a binary that may be older or newer than the one
    /// that wrote it. A version it does not recognise is refused by name rather
    /// than parsed optimistically: the failure mode of guessing is a deployment
    /// that thinks it knows what the project declares.
    [<Literal>]
    let FormatVersion = 1

    // ---- reading helpers ----------------------------------------------------
    //
    // Deliberately thin. A missing or mistyped property throws, and `read`
    // turns that into one clear failure — an artifact is machine-written, so a
    // malformed one means tampering or version skew, not a typo to recover
    // from field by field.

    /// `JsonElement.GetProperty` throws a `KeyNotFoundException` that does not
    /// say which key, and the commonest way to reach it is an artifact written
    /// by a build that did not have the field yet. Naming it turns "the given
    /// key was not present in the dictionary" into something a reader can act
    /// on.
    let private prop (name: string) (e: JsonElement) =
        match e.TryGetProperty name with
        | true, value -> value
        | _ -> failwithf "no '%s'" name

    let private getString (name: string) (e: JsonElement) = (prop name e).GetString()

    let private getBool (name: string) (e: JsonElement) = (prop name e).GetBoolean()

    let private getInt (name: string) (e: JsonElement) = (prop name e).GetInt32()

    let private getInt64 (name: string) (e: JsonElement) = (prop name e).GetInt64()

    let private items (name: string) (e: JsonElement) =
        (prop name e).EnumerateArray() |> List.ofSeq

    let private getStrings (name: string) (e: JsonElement) =
        items name e |> List.map (fun i -> i.GetString())

    let private optional (name: string) (e: JsonElement) =
        let p = prop name e
        if p.ValueKind = JsonValueKind.Null then None else Some p

    let private optionalString (name: string) (e: JsonElement) =
        optional name e |> Option.map (fun p -> p.GetString())

    /// A `(string * string) list`, rendered as an array of pairs rather than an
    /// object: JSON object keys have no defined order and Strata's output is
    /// byte-stable by contract (NFR-001).
    let private renderPairs (pairs: (string * string) list) =
        JArray [ for key, value in pairs -> JObject [ "key", JString key; "value", JString value ] ]

    let private readPairs (name: string) (e: JsonElement) =
        items name e |> List.map (fun i -> getString "key" i, getString "value" i)

    let private renderStrings (values: string list) = JArray [ for v in values -> JString v ]

    // ---- Identity -----------------------------------------------------------
    //
    // `Wire.identifier` renders a third member, `display`, for human readers of
    // `--json` output. This format has no human to serve and a reader that must
    // not have two sources of truth for the same name, so it renders the two
    // fields the type actually has.

    let renderIdentifier (id: Identifier) =
        JObject [ "text", JString id.Text; "quoted", JBool id.WasQuoted ]

    let readIdentifier (e: JsonElement) : Identifier =
        if getBool "quoted" e then
            Identifier.quoted (getString "text" e)
        else
            Identifier.unquoted (getString "text" e)

    let renderQualifiedName (q: QualifiedName) =
        JObject [ "schema", (match q.Schema with Some s -> renderIdentifier s | None -> JNull)
                  "name", renderIdentifier q.Name ]

    let readQualifiedName (e: JsonElement) : QualifiedName =
        match optional "schema" e with
        | Some s -> QualifiedName.qualified (readIdentifier s) (readIdentifier (prop "name" e))
        | None -> QualifiedName.unqualified (readIdentifier (prop "name" e))

    let private renderNames (names: Identifier list) = JArray(names |> List.map renderIdentifier)

    let private readNames (name: string) (e: JsonElement) =
        items name e |> List.map readIdentifier

    // ---- Scope and completeness ---------------------------------------------

    let renderScope (scope: ManagementScope) =
        JString(
            match scope with
            | Managed -> "managed"
            | Observed -> "observed"
            | ExtensionOwned -> "extension-owned"
            | SystemOwned -> "system-owned"
            | UnknownOwnership -> "unknown")

    let readScope (e: JsonElement) : ManagementScope =
        match e.GetString() with
        | "managed" -> Managed
        | "observed" -> Observed
        | "extension-owned" -> ExtensionOwned
        | "system-owned" -> SystemOwned
        | "unknown" -> UnknownOwnership
        | other -> failwithf "unknown management scope '%s'" other

    /// `Partial` and `Inaccessible` carry a reason, and the reason is the point:
    /// ER-008 keeps them apart from each other and from `Complete`, so the wire
    /// form keeps them apart too.
    let renderCategoryState (state: CategoryState) =
        match state with
        | Complete -> JObject [ "state", JString "complete" ]
        | NotRequested -> JObject [ "state", JString "not-requested" ]
        | Partial reason -> JObject [ "state", JString "partial"; "reason", JString reason ]
        | Inaccessible reason -> JObject [ "state", JString "inaccessible"; "reason", JString reason ]

    let readCategoryState (e: JsonElement) : CategoryState =
        match getString "state" e with
        | "complete" -> Complete
        | "not-requested" -> NotRequested
        | "partial" -> Partial(getString "reason" e)
        | "inaccessible" -> Inaccessible(getString "reason" e)
        | other -> failwithf "unknown completeness state '%s'" other

    let renderCompleteness (c: Completeness) =
        JArray [ for name, state in c.Categories ->
                   JObject [ "category", JString name; "state", renderCategoryState state ] ]

    let readCompleteness (name: string) (e: JsonElement) : Completeness =
        items name e
        |> List.map (fun i -> getString "category" i, readCategoryState (prop "state" i))
        |> Completeness.ofList

    // ---- Evidence -----------------------------------------------------------

    let renderCertainty (c: Certainty) =
        JString(
            match c with
            | Certain -> "certain"
            | High -> "high"
            | Medium -> "medium"
            | Low -> "low")

    let readCertainty (e: JsonElement) : Certainty =
        match e.GetString() with
        | "certain" -> Certain
        | "high" -> High
        | "medium" -> Medium
        | "low" -> Low
        | other -> failwithf "unknown certainty '%s'" other

    let renderEvidenceSource (source: EvidenceSource) =
        match source with
        | Catalog relation -> JObject [ "kind", JString "catalog"; "detail", JString relation ]
        | ForeignKeyConstraint name -> JObject [ "kind", JString "foreign-key"; "detail", JString name ]
        | ViewDefinition view -> JObject [ "kind", JString "view-definition"; "detail", JString view ]
        | RoutineBody routine -> JObject [ "kind", JString "routine-body"; "detail", JString routine ]
        | SqlUnit sourceId -> JObject [ "kind", JString "sql-unit"; "detail", JString sourceId ]
        | ManualDeclaration by -> JObject [ "kind", JString "manual"; "detail", JString by ]

    let readEvidenceSource (e: JsonElement) : EvidenceSource =
        let detail = getString "detail" e

        match getString "kind" e with
        | "catalog" -> Catalog detail
        | "foreign-key" -> ForeignKeyConstraint detail
        | "view-definition" -> ViewDefinition detail
        | "routine-body" -> RoutineBody detail
        | "sql-unit" -> SqlUnit detail
        | "manual" -> ManualDeclaration detail
        | other -> failwithf "unknown evidence source '%s'" other

    let renderEvidenceItem (item: EvidenceItem) =
        JObject [ "source", renderEvidenceSource item.Source; "detail", JString item.Detail ]

    let readEvidenceItem (e: JsonElement) : EvidenceItem =
        { Source = readEvidenceSource (prop "source" e); Detail = getString "detail" e }

    // ---- Columns and constraints --------------------------------------------

    let renderTypeRef (t: TypeRef) =
        JObject [ "typeName", renderQualifiedName t.TypeName; "nullable", JBool t.IsNullable ]

    let readTypeRef (e: JsonElement) : TypeRef =
        { TypeName = readQualifiedName (prop "typeName" e); IsNullable = getBool "nullable" e }

    let renderColumn (c: Column) =
        JObject [ "name", renderIdentifier c.Name
                  "type", renderTypeRef c.Type
                  "position", JInt c.Position
                  "hasDefault", JBool c.HasDefault
                  "default", (match c.DefaultExpression with Some d -> JString d | None -> JNull)
                  "generated", JBool c.IsGenerated
                  "identity", JBool c.IsIdentity ]

    let readColumn (e: JsonElement) : Column =
        { Name = readIdentifier (prop "name" e)
          Type = readTypeRef (prop "type" e)
          Position = getInt "position" e
          HasDefault = getBool "hasDefault" e
          DefaultExpression = optionalString "default" e
          IsGenerated = getBool "generated" e
          IsIdentity = getBool "identity" e }

    /// An unnamed constraint is `None`, never an empty name. WI-0054: a
    /// fabricated name made every such project churn forever.
    let private renderConstraintName (name: ConstraintName) =
        match name with
        | Some n -> renderIdentifier n
        | None -> JNull

    let private readConstraintName (e: JsonElement) : ConstraintName =
        optional "constraintName" e |> Option.map readIdentifier

    let renderPrimaryKey (pk: PrimaryKey) =
        JObject [ "constraintName", renderConstraintName pk.ConstraintName
                  "columns", renderNames pk.Columns ]

    let readPrimaryKey (e: JsonElement) : PrimaryKey =
        { ConstraintName = readConstraintName e; Columns = readNames "columns" e }

    let renderUnique (u: UniqueConstraint) =
        JObject [ "constraintName", renderConstraintName u.ConstraintName
                  "columns", renderNames u.Columns ]

    let readUnique (e: JsonElement) : UniqueConstraint =
        { ConstraintName = readConstraintName e; Columns = readNames "columns" e }

    let renderCheck (c: CheckConstraint) =
        JObject [ "constraintName", renderConstraintName c.ConstraintName
                  "expression", JString c.Expression ]

    let readCheck (e: JsonElement) : CheckConstraint =
        { ConstraintName = readConstraintName e; Expression = getString "expression" e }

    let renderForeignKey (f: ForeignKey) =
        JObject [ "constraintName", renderConstraintName f.ConstraintName
                  "columns", renderNames f.Columns
                  "referencedTable", renderQualifiedName f.ReferencedTable
                  "referencedColumns", renderNames f.ReferencedColumns ]

    let readForeignKey (e: JsonElement) : ForeignKey =
        { ConstraintName = readConstraintName e
          Columns = readNames "columns" e
          ReferencedTable = readQualifiedName (prop "referencedTable" e)
          ReferencedColumns = readNames "referencedColumns" e }

    let renderIndex (i: Index) =
        JObject [ "name", renderIdentifier i.Name
                  "columns", renderNames i.Columns
                  "unique", JBool i.IsUnique
                  "predicate", (match i.Predicate with Some p -> JString p | None -> JNull) ]

    let readIndex (e: JsonElement) : Index =
        { Name = readIdentifier (prop "name" e)
          Columns = readNames "columns" e
          IsUnique = getBool "unique" e
          Predicate = optionalString "predicate" e }

    // ---- Triggers -----------------------------------------------------------

    let renderTrigger (t: Trigger) =
        JObject [ "name", renderIdentifier t.Name
                  "timing",
                  JString(
                      match t.Timing with
                      | TriggerTiming.Before -> "before"
                      | TriggerTiming.After -> "after"
                      | TriggerTiming.InsteadOf -> "instead-of")
                  "events", renderStrings t.Events
                  "level",
                  JString(
                      match t.Level with
                      | TriggerLevel.Row -> "row"
                      | TriggerLevel.Statement -> "statement")
                  "updateColumns", renderNames t.UpdateColumns
                  "function", renderQualifiedName t.Function
                  "arguments", renderStrings t.Arguments
                  "hasCondition", JBool t.HasCondition ]

    let readTrigger (e: JsonElement) : Trigger =
        { Name = readIdentifier (prop "name" e)
          Timing =
            (match getString "timing" e with
             | "before" -> TriggerTiming.Before
             | "after" -> TriggerTiming.After
             | "instead-of" -> TriggerTiming.InsteadOf
             | other -> failwithf "unknown trigger timing '%s'" other)
          Events = getStrings "events" e
          Level =
            (match getString "level" e with
             | "row" -> TriggerLevel.Row
             | "statement" -> TriggerLevel.Statement
             | other -> failwithf "unknown trigger level '%s'" other)
          UpdateColumns = readNames "updateColumns" e
          Function = readQualifiedName (prop "function" e)
          Arguments = getStrings "arguments" e
          HasCondition = getBool "hasCondition" e }

    // ---- Objects ------------------------------------------------------------

    let renderTable (t: Table) =
        JObject [ "name", renderQualifiedName t.Name
                  "columns", JArray(t.Columns |> List.map renderColumn)
                  "primaryKey", (match t.PrimaryKey with Some pk -> renderPrimaryKey pk | None -> JNull)
                  "uniqueConstraints", JArray(t.UniqueConstraints |> List.map renderUnique)
                  "checkConstraints", JArray(t.CheckConstraints |> List.map renderCheck)
                  "foreignKeys", JArray(t.ForeignKeys |> List.map renderForeignKey)
                  "indexes", JArray(t.Indexes |> List.map renderIndex)
                  "triggers", JArray(t.Triggers |> List.map renderTrigger)
                  "scope", renderScope t.Scope ]

    let readTable (e: JsonElement) : Table =
        { Name = readQualifiedName (prop "name" e)
          Columns = items "columns" e |> List.map readColumn
          PrimaryKey = optional "primaryKey" e |> Option.map readPrimaryKey
          UniqueConstraints = items "uniqueConstraints" e |> List.map readUnique
          CheckConstraints = items "checkConstraints" e |> List.map readCheck
          ForeignKeys = items "foreignKeys" e |> List.map readForeignKey
          Indexes = items "indexes" e |> List.map readIndex
          Triggers = items "triggers" e |> List.map readTrigger
          Scope = readScope (prop "scope" e) }

    let renderView (v: View) =
        JObject [ "name", renderQualifiedName v.Name
                  "columns", JArray(v.Columns |> List.map renderColumn)
                  "materialized", JBool v.IsMaterialized
                  "definition", JString v.Definition
                  "scope", renderScope v.Scope ]

    let readView (e: JsonElement) : View =
        { Name = readQualifiedName (prop "name" e)
          Columns = items "columns" e |> List.map readColumn
          IsMaterialized = getBool "materialized" e
          Definition = getString "definition" e
          Scope = readScope (prop "scope" e) }

    let renderRoutine (r: Routine) =
        JObject [ "name", renderQualifiedName r.Name
                  "kind",
                  JString(
                      match r.Kind with
                      | Function -> "function"
                      | Procedure -> "procedure")
                  "argumentTypes", renderStrings r.ArgumentTypes
                  "returnType", (match r.ReturnType with Some t -> JString t | None -> JNull)
                  "language", JString r.Language
                  "body", (match r.Body with Some b -> JString b | None -> JNull)
                  "scope", renderScope r.Scope ]

    let readRoutine (e: JsonElement) : Routine =
        { Name = readQualifiedName (prop "name" e)
          Kind =
            (match getString "kind" e with
             | "function" -> Function
             | "procedure" -> Procedure
             | other -> failwithf "unknown routine kind '%s'" other)
          ArgumentTypes = getStrings "argumentTypes" e
          ReturnType = optionalString "returnType" e
          Language = getString "language" e
          Body = optionalString "body" e
          Scope = readScope (prop "scope" e) }

    let renderSequence (s: Sequence) =
        JObject [ "name", renderQualifiedName s.Name
                  "dataType", JString s.DataType
                  // int64 out of range for JInt, and a sequence bound routinely
                  // is: bigint's maximum is the DEFAULT maxValue. Rendered as a
                  // string so no reader has to guess a numeric width.
                  "start", JString(string s.Start)
                  "increment", JString(string s.Increment)
                  "minValue", JString(string s.MinValue)
                  "maxValue", JString(string s.MaxValue)
                  "cache", JString(string s.Cache)
                  "cycle", JBool s.Cycle
                  "scope", renderScope s.Scope ]

    let private int64Of (name: string) (e: JsonElement) =
        Int64.Parse(getString name e, Globalization.CultureInfo.InvariantCulture)

    let readSequence (e: JsonElement) : Sequence =
        { Name = readQualifiedName (prop "name" e)
          DataType = getString "dataType" e
          Start = int64Of "start" e
          Increment = int64Of "increment" e
          MinValue = int64Of "minValue" e
          MaxValue = int64Of "maxValue" e
          Cache = int64Of "cache" e
          Cycle = getBool "cycle" e
          Scope = readScope (prop "scope" e) }

    let renderObject (o: SchemaObject) =
        match o with
        | TableObject t -> JObject [ "kind", JString "table"; "table", renderTable t ]
        | ViewObject v -> JObject [ "kind", JString "view"; "view", renderView v ]
        | RoutineObject r -> JObject [ "kind", JString "routine"; "routine", renderRoutine r ]
        | SequenceObject s -> JObject [ "kind", JString "sequence"; "sequence", renderSequence s ]

    let readObject (e: JsonElement) : SchemaObject =
        match getString "kind" e with
        | "table" -> TableObject(readTable (prop "table" e))
        | "view" -> ViewObject(readView (prop "view" e))
        | "routine" -> RoutineObject(readRoutine (prop "routine" e))
        | "sequence" -> SequenceObject(readSequence (prop "sequence" e))
        | other -> failwithf "unknown object kind '%s'" other

    // ---- Grants, extensions, policies ---------------------------------------

    let renderGrantTarget (target: GrantTarget) =
        match target with
        | GrantTarget.Relation n -> JObject [ "kind", JString "relation"; "name", renderQualifiedName n ]
        | GrantTarget.Schema s -> JObject [ "kind", JString "schema"; "schema", renderIdentifier s ]
        | GrantTarget.Routine (n, args) ->
            JObject [ "kind", JString "routine"
                      "name", renderQualifiedName n
                      "argumentTypes", renderStrings args ]
        | GrantTarget.RelationColumn (n, c) ->
            JObject [ "kind", JString "relation-column"
                      "name", renderQualifiedName n
                      "column", renderIdentifier c ]

    let readGrantTarget (e: JsonElement) : GrantTarget =
        match getString "kind" e with
        | "relation" -> GrantTarget.Relation(readQualifiedName (prop "name" e))
        | "schema" -> GrantTarget.Schema(readIdentifier (prop "schema" e))
        | "routine" -> GrantTarget.Routine(readQualifiedName (prop "name" e), getStrings "argumentTypes" e)
        | "relation-column" ->
            GrantTarget.RelationColumn(readQualifiedName (prop "name" e), readIdentifier (prop "column" e))
        | other -> failwithf "unknown grant target '%s'" other

    let renderGrant (g: Grant) =
        JObject [ "target", renderGrantTarget g.Target
                  "grantee", JString g.Grantee
                  "privileges", renderStrings g.Privileges
                  "grantable", renderStrings g.Grantable ]

    let readGrant (e: JsonElement) : Grant =
        { Target = readGrantTarget (prop "target" e)
          Grantee = getString "grantee" e
          Privileges = getStrings "privileges" e
          Grantable = getStrings "grantable" e }

    let renderExtension (x: Extension) =
        JObject [ "name", renderIdentifier x.Name
                  "schema", (match x.Schema with Some s -> renderIdentifier s | None -> JNull)
                  "version", (match x.Version with Some v -> JString v | None -> JNull)
                  "relocatable", JBool x.IsRelocatable ]

    let readExtension (e: JsonElement) : Extension =
        { Name = readIdentifier (prop "name" e)
          Schema = optional "schema" e |> Option.map readIdentifier
          Version = optionalString "version" e
          IsRelocatable = getBool "relocatable" e }

    /// `Using` and `WithCheck` have THREE states, and the wire form keeps all
    /// three: absent (`null`), written but not rendered by the server (`""`),
    /// and rendered. Collapsing the middle one into either neighbour is the
    /// defect that made a changed policy expression produce zero changes.
    let renderPolicy (p: Policy) =
        JObject [ "name", renderIdentifier p.Name
                  "command",
                  JString(
                      match p.Command with
                      | PolicyCommand.All -> "all"
                      | PolicyCommand.Select -> "select"
                      | PolicyCommand.Insert -> "insert"
                      | PolicyCommand.Update -> "update"
                      | PolicyCommand.Delete -> "delete")
                  "permissive", JBool p.IsPermissive
                  "roles", renderStrings p.Roles
                  "using", (match p.Using with Some u -> JString u | None -> JNull)
                  "withCheck", (match p.WithCheck with Some w -> JString w | None -> JNull) ]

    let readPolicy (e: JsonElement) : Policy =
        { Name = readIdentifier (prop "name" e)
          Command =
            (match getString "command" e with
             | "all" -> PolicyCommand.All
             | "select" -> PolicyCommand.Select
             | "insert" -> PolicyCommand.Insert
             | "update" -> PolicyCommand.Update
             | "delete" -> PolicyCommand.Delete
             | other -> failwithf "unknown policy command '%s'" other)
          IsPermissive = getBool "permissive" e
          Roles = getStrings "roles" e
          Using = optionalString "using" e
          WithCheck = optionalString "withCheck" e }

    let renderRowSecuritySetting (s: RowSecuritySetting) =
        JString(
            match s with
            | RowSecuritySetting.Enable -> "enable"
            | RowSecuritySetting.Disable -> "disable"
            | RowSecuritySetting.Force -> "force"
            | RowSecuritySetting.NoForce -> "no-force")

    let readRowSecuritySetting (e: JsonElement) : RowSecuritySetting =
        match e.GetString() with
        | "enable" -> RowSecuritySetting.Enable
        | "disable" -> RowSecuritySetting.Disable
        | "force" -> RowSecuritySetting.Force
        | "no-force" -> RowSecuritySetting.NoForce
        | other -> failwithf "unknown row security setting '%s'" other

    // ---- Snapshot -----------------------------------------------------------

    let renderServerVersion (v: ServerVersion) =
        JObject [ "major", JInt v.Major; "full", JString v.Full ]

    let readServerVersion (e: JsonElement) : ServerVersion =
        { Major = getInt "major" e; Full = getString "full" e }

    /// A `Fact` has a private constructor that refuses empty evidence, so the
    /// reader goes through `Fact.create` and an artifact carrying a fact with
    /// no evidence is refused rather than fabricated.
    let renderServerVersionFact (f: Fact<ServerVersion>) =
        JObject [ "value", renderServerVersion (Fact.value f)
                  "certainty", renderCertainty (Fact.certainty f)
                  "evidence", JArray(Fact.evidence f |> List.map renderEvidenceItem) ]

    let readServerVersionFact (e: JsonElement) : Fact<ServerVersion> =
        let evidence = items "evidence" e |> List.map readEvidenceItem

        match Fact.create (readCertainty (prop "certainty" e)) evidence (readServerVersion (prop "value" e)) with
        | Some fact -> fact
        | None -> failwith "the artifact carries a server version with no evidence"

    let renderSnapshot (s: SchemaSnapshot) =
        JObject [ "objects", JArray(s.Objects |> List.map renderObject)
                  "serverVersion", (match s.ServerVersion with Some f -> renderServerVersionFact f | None -> JNull)
                  "completeness", renderCompleteness s.Completeness ]

    let readSnapshot (e: JsonElement) : SchemaSnapshot =
        { Objects = items "objects" e |> List.map readObject
          ServerVersion = optional "serverVersion" e |> Option.map readServerVersionFact
          Completeness = readCompleteness "completeness" e }

    // ---- The declared project -----------------------------------------------

    let private renderDeclaration (name: QualifiedName, text: string) =
        JObject [ "name", renderQualifiedName name; "text", JString text ]

    let private readDeclaration (e: JsonElement) =
        readQualifiedName (prop "name" e), getString "text" e

    /// Keyed by table AND name, because a trigger's or a policy's name is scoped
    /// to its table rather than to a schema: two tables may each carry a
    /// `set_updated_at` and neither is the other.
    let private renderTableScoped (table: QualifiedName, name: Identifier, text: string) =
        JObject [ "table", renderQualifiedName table
                  "name", renderIdentifier name
                  "text", JString text ]

    let private readTableScoped (e: JsonElement) =
        (readQualifiedName (prop "table" e), readIdentifier (prop "name" e)), getString "text" e

    let renderDeclaredData (d: DesiredState.DeclaredData) =
        JObject [ "table", renderQualifiedName d.Table
                  "columns", renderNames d.Columns
                  "rows", JArray [ for row in d.Rows -> renderStrings row ]
                  "path", JString d.Path ]

    let readDeclaredData (e: JsonElement) : DesiredState.DeclaredData =
        { Table = readQualifiedName (prop "table" e)
          Columns = readNames "columns" e
          Rows = items "rows" e |> List.map (fun r -> r.EnumerateArray() |> Seq.map (fun v -> v.GetString()) |> List.ofSeq)
          Path = getString "path" e }

    let renderLoadFailure (f: DesiredState.LoadFailure) =
        JObject [ "path", JString f.Path; "reason", JString f.Reason ]

    let readLoadFailure (e: JsonElement) : DesiredState.LoadFailure =
        { Path = getString "path" e; Reason = getString "reason" e }

    let renderLoaded (l: DesiredState.Loaded) =
        JObject [ "snapshot", renderSnapshot l.Snapshot
                  "failures", JArray(l.Failures |> List.map renderLoadFailure)
                  "declarations", JArray(l.Declarations |> List.map renderDeclaration)
                  "triggerDeclarations",
                  JArray(l.TriggerDeclarations |> List.map (fun ((t, n), text) -> renderTableScoped (t, n, text)))
                  "data", JArray(l.Data |> List.map renderDeclaredData)
                  "grants", JArray(l.Grants |> List.map renderGrant)
                  "policies",
                  JArray(l.Policies |> List.map (fun (t, p) ->
                      JObject [ "table", renderQualifiedName t; "policy", renderPolicy p ]))
                  "policyDeclarations",
                  JArray(l.PolicyDeclarations |> List.map (fun ((t, n), text) -> renderTableScoped (t, n, text)))
                  "rowSecurity",
                  JArray(l.RowSecurity |> List.map (fun (t, s) ->
                      JObject [ "table", renderQualifiedName t; "setting", renderRowSecuritySetting s ]))
                  "extensions", JArray(l.Extensions |> List.map renderExtension) ]

    let readLoaded (e: JsonElement) : DesiredState.Loaded =
        { Snapshot = readSnapshot (prop "snapshot" e)
          Failures = items "failures" e |> List.map readLoadFailure
          Declarations = items "declarations" e |> List.map readDeclaration
          TriggerDeclarations = items "triggerDeclarations" e |> List.map readTableScoped
          Data = items "data" e |> List.map readDeclaredData
          Grants = items "grants" e |> List.map readGrant
          Policies =
            items "policies" e
            |> List.map (fun i -> readQualifiedName (prop "table" i), readPolicy (prop "policy" i))
          PolicyDeclarations = items "policyDeclarations" e |> List.map readTableScoped
          RowSecurity =
            items "rowSecurity" e
            |> List.map (fun i -> readQualifiedName (prop "table" i), readRowSecuritySetting (prop "setting" i))
          Extensions = items "extensions" e |> List.map readExtension }

    // ---- The normalisations -------------------------------------------------

    let renderNormalisedTable (n: SchemaDiff.NormalisedTable) =
        JObject [ "table", JString n.Table
                  "defaults", renderPairs n.Defaults
                  "checks", renderPairs n.Checks ]

    let readNormalisedTable (e: JsonElement) : SchemaDiff.NormalisedTable =
        { Table = getString "table" e
          Defaults = readPairs "defaults" e
          Checks = readPairs "checks" e }

    let renderResolvedRow (r: SchemaDiff.ResolvedRow) =
        JObject [ "key", JString r.Key
                  "rendered", JString r.Rendered
                  "literals", renderStrings r.Literals ]

    let readResolvedRow (e: JsonElement) : SchemaDiff.ResolvedRow =
        { Key = getString "key" e
          Rendered = getString "rendered" e
          Literals = getStrings "literals" e }

    let renderResolvedData (d: SchemaDiff.ResolvedData) =
        JObject [ "table", JString d.Table
                  "columns", renderStrings d.Columns
                  "keyColumns", renderStrings d.KeyColumns
                  "declared", JArray(d.Declared |> List.map renderResolvedRow)
                  "deployed", JArray(d.Deployed |> List.map renderResolvedRow) ]

    let readResolvedData (e: JsonElement) : SchemaDiff.ResolvedData =
        { Table = getString "table" e
          Columns = getStrings "columns" e
          KeyColumns = getStrings "keyColumns" e
          Declared = items "declared" e |> List.map readResolvedRow
          Deployed = items "deployed" e |> List.map readResolvedRow }

    let renderDataFailure (f: SchemaDiff.DataFailure) =
        JObject [ "table", JString f.Table; "reason", JString f.Reason ]

    let readDataFailure (e: JsonElement) : SchemaDiff.DataFailure =
        { Table = getString "table" e; Reason = getString "reason" e }

    // ---- The artifact -------------------------------------------------------

    /// The whole artifact as JSON.
    ///
    /// ## Resolved reference rows are NOT in here, and that is the point
    ///
    /// `ResolvedDesiredState.Data` holds declared rows and DEPLOYED rows side by
    /// side, rendered by the server through the real column types. The deployed
    /// half is read from a live table, so it describes a TARGET rather than a
    /// project — and an artifact that carried it would be carrying one database's
    /// contents to another.
    ///
    /// It did, briefly, and the failure was silent in the worst way. An artifact
    /// compiled against a database that already held the reference rows recorded
    /// "declared and deployed agree". Deploying it to an EMPTY database created
    /// the table and inserted nothing: four statements instead of six, exit 0,
    /// and a lookup table with no rows in it. Nothing in the output was wrong,
    /// because nothing in the artifact was wrong — it was an answer to a
    /// question about a different server.
    ///
    /// So the artifact carries the DECLARED rows only, inside `declared.data`,
    /// as the literal tokens the author wrote. `deploy` resolves them against
    /// its own target, which is the same thing `plan` does and the only thing
    /// that can be right for more than one target. `Data` and `DataFailures`
    /// therefore read back EMPTY, and the caller is expected to fill them.
    let render (r: ResolvedDesiredState) =
        JObject [ "formatVersion", JInt FormatVersion
                  "declared", renderLoaded r.Declared
                  "normalisedViews", renderPairs r.NormalisedViews
                  "normalisedTables", JArray(r.NormalisedTables |> List.map renderNormalisedTable)
                  "policies",
                  JArray(r.Policies |> List.map (fun (t, p) ->
                      JObject [ "table", renderQualifiedName t; "policy", renderPolicy p ]))
                  "rowSecurity",
                  JArray(r.RowSecurity |> List.map (fun (t, s) ->
                      JObject [ "table", renderQualifiedName t; "setting", renderRowSecuritySetting s ]))
                  "warnings", renderStrings r.Warnings
                  "compiledWith", (match r.CompiledWith with Some v -> renderServerVersion v | None -> JNull) ]

    /// The artifact's text. Deterministic by construction (NFR-001): every key
    /// order is declared above, every collection keeps the order resolution gave
    /// it, and nothing here reads a clock, a GUID or a machine name.
    let toText (r: ResolvedDesiredState) = Json.render (render r)

    /// Read an artifact back.
    ///
    /// Every failure is one message naming the artifact, because there is no
    /// partial success worth having: a caller that got half a desired state
    /// would deploy half a project.
    let ofText (text: string) : Result<ResolvedDesiredState, string> =
        try
            use document = JsonDocument.Parse text
            let root = document.RootElement

            let version =
                match root.TryGetProperty "formatVersion" with
                | true, v -> v.GetInt32()
                | _ -> failwith "no formatVersion; this is not a Strata artifact"

            if version <> FormatVersion then
                Microsoft.FSharp.Core.Error(
                    sprintf
                        "artifact format version %d, but this build of Strata writes and reads version %d. Recompile the project with this build."
                        version
                        FormatVersion)
            else

            Ok
                { Declared = readLoaded (prop "declared" root)
                  NormalisedViews = readPairs "normalisedViews" root
                  NormalisedTables = items "normalisedTables" root |> List.map readNormalisedTable
                  Policies =
                    items "policies" root
                    |> List.map (fun i -> readQualifiedName (prop "table" i), readPolicy (prop "policy" i))
                  RowSecurity =
                    items "rowSecurity" root
                    |> List.map (fun i -> readQualifiedName (prop "table" i), readRowSecuritySetting (prop "setting" i))
                  // Target-specific, so not carried. The caller resolves them
                  // against the target it is deploying to.
                  Data = []
                  DataFailures = []
                  Warnings = getStrings "warnings" root
                  CompiledWith = optional "compiledWith" root |> Option.map readServerVersion }
        with ex ->
            Microsoft.FSharp.Core.Error(sprintf "the artifact could not be read (%s)" ex.Message)
