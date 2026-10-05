namespace Strata.Application.SchemaDiff

open System
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.DialectPort
open Strata.Analysis.ProposedChange

module Schemas =

    /// Schemas a project declares objects in that the database does not have.
    ///
    /// `existing` is `None` when the schema list could not be read, and that is
    /// not an empty list: a caller that cannot see the schemas must not
    /// conclude one is missing and propose creating it. It is disclosed instead.
    ///
    /// Only schemas something is actually DECLARED in are created. A directory
    /// that contributes a managed-schema name but no loaded object is a schema
    /// the project has not shown a use for, and creating it would be acting on
    /// a name rather than on a declaration.
    ///
    /// There is no counterpart for removal, deliberately. A schema is a
    /// container: dropping one takes everything inside it, including objects
    /// the project never declared and so never claimed.
    ///
    /// What the project manages and what it declared objects in are one fact,
    /// derived from one directory tree (`DF-STRATA-2026-C3A2`), so the managed
    /// schemas are the declared ones.
    let compare (inputs: Inputs) : FamilyDiff =
        let declaredIn = inputs.ManagedSchemas

        match inputs.ExistingSchemas with
        | None ->
            { Differences = []
              Disclosures =
                if List.isEmpty declaredIn then
                    []
                else
                    [ { Object = QualifiedName.unqualified (Identifier.unquoted "(schemas)")
                        Reason = NotCompared
                        Detail =
                          "the database's schema list could not be read, so whether the declared schemas exist was NOT checked" } ] }
        | Some present ->
            let folded = present |> List.map (fun s -> s.ToLowerInvariant())

            declaredIn
            |> List.filter (fun d -> not (List.contains (d.ToLowerInvariant()) folded))
            |> List.map (fun d -> Ok(CreateSchema(Identifier.unquoted d)))
            |> FamilyDiff.ofDifferences
