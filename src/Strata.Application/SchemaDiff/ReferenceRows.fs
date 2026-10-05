namespace Strata.Application.SchemaDiff

open System
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.DialectPort
open Strata.Analysis.ProposedChange

module ReferenceRows =

    /// Differences between declared reference rows and deployed ones.
    ///
    /// Matching is by KEY, which is the table's primary key — the identity the
    /// database itself asserts. Equality is by the server's rendering of the
    /// declared columns, so a column the file does not name takes no part.
    ///
    /// ## A row nobody declared is not a row to delete
    ///
    /// This is `NG-006` again, and the stakes are higher than for an object. A
    /// reference row that user data points at cannot be removed without either
    /// failing on the foreign key or, worse, cascading into that data. So an
    /// undeclared row is REPORTED and never proposed for removal — not even
    /// under `--allow-drops`, which authorises dropping objects a project owns,
    /// not deleting rows it never claimed.
    let compare (inputs: Inputs) : FamilyDiff =
        let data = inputs.Data
        let failures = inputs.DataFailures
        let nameOf (table: string) =
            match table.Split('.') with
            | [| schema; object' |] ->
                QualifiedName.qualified (Identifier.unquoted schema) (Identifier.unquoted object')
            | _ -> QualifiedName.unqualified (Identifier.unquoted table)

        let changes =
            data
            |> List.collect (fun d ->
                let table = nameOf d.Table
                let deployedByKey = d.Deployed |> List.map (fun r -> r.Key, r) |> Map.ofList

                d.Declared
                |> List.choose (fun declared ->
                    match Map.tryFind declared.Key deployedByKey with
                    | None -> Some(Ok(InsertRow(table, declared.Key)))
                    | Some deployed when deployed.Rendered <> declared.Rendered ->
                        Some(Ok(UpdateRow(table, declared.Key)))
                    | Some _ -> None))

        let undeclared =
            data
            |> List.choose (fun d ->
                let declaredKeys = d.Declared |> List.map (fun r -> r.Key) |> Set.ofList
                let extra = d.Deployed |> List.filter (fun r -> not (Set.contains r.Key declaredKeys))

                if List.isEmpty extra then
                    None
                else
                    Some
                        { Object = nameOf d.Table
                          Reason = NotModelled
                          Detail =
                            sprintf
                                "%d row(s) in this table are not declared by the project: %s. They are NOT proposed for deletion — user data may reference them."
                                (List.length extra)
                                (extra |> List.map (fun r -> r.Key) |> List.sort |> String.concat ", ") })

        let unresolved =
            failures
            |> List.map (fun f ->
                { Object = nameOf f.Table
                  Reason = NotCompared
                  Detail = sprintf "declared rows were NOT compared: %s" f.Reason })

        { Differences = changes; Disclosures = undeclared @ unresolved }

    /// The one explanation PostgreSQL will not give you.
    ///
    /// A plan that adds an enum label AND declares reference rows using it
    /// cannot do both at once, and the reason is not obvious from either
    /// error. Resolving the rows fails with `invalid input value for enum`,
    /// because the label does not exist yet; and if Strata created it in the
    /// shadow so the rows resolved, the APPLY would fail instead, with
    /// `unsafe use of new value` — PostgreSQL refuses to let a value added
    /// inside a transaction be used in that same transaction, and Strata
    /// applies a plan as one transaction.
    ///
    /// So the two-step is PostgreSQL's, not Strata's, and the honest thing
    /// is to say which step this is. Without this the operator sees a raw
    /// 22P02 and has no reason to think running again would help.
    let enumTwoStep (enums: FamilyDiff) (dataFailures: DataFailure list) : Suppression list =
        let addedValues =
            enums.Differences
            |> List.choose (function
                | Ok (AddEnumValue (name, value, _)) -> Some(QualifiedName.display name, value)
                | _ -> None)

        if List.isEmpty addedValues || List.isEmpty dataFailures then
            []
        else
            dataFailures
            |> List.filter (fun f ->
                addedValues |> List.exists (fun (_, value) -> f.Reason.Contains value))
            |> List.map (fun f ->
                { Object = QualifiedName.unqualified (Identifier.unquoted f.Table)
                  Reason = NotCompared
                  Detail =
                    sprintf
                        "this plan adds %s AND declares rows using it. PostgreSQL will not let a value added inside a transaction be used in that same transaction, so the two cannot happen together: this run adds the value, and the rows land on the next one. Nothing is lost and no manual step is needed — re-run after this applies."
                        (addedValues
                         |> List.filter (fun (_, value) -> f.Reason.Contains value)
                         |> List.map (fun (typeName, value) -> sprintf "'%s' to %s" value typeName)
                         |> List.distinct
                         |> String.concat " and ") })
